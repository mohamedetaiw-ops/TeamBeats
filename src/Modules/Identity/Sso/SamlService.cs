using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Sso;

/// <summary>What a verified SAML assertion says about the person, before any product rule is applied.</summary>
public sealed record SamlAssertion(string Issuer, string NameId, string? NameIdFormat, IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes,
    string? AuthnContext, DateTimeOffset? AuthnInstant);

/// <summary>Why a SAML response was refused. <see cref="Reason"/> is a short code for audit and the admin, never response content.</summary>
public sealed class SamlRejectedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// SAML 2.0 service provider (spec 003 US4, US5; IAM-006), SP-initiated with the HTTP-Redirect binding out and
/// HTTP-POST back. The browser is bound to the pending request by a cookie, the request ID is single use and must be
/// the response's <c>InResponseTo</c> (replay), and only a signature by one of the tenant's configured IdP certificates
/// over the one element whose ID the signature references is trusted (signature wrapping). Audience, recipient,
/// destination and time windows are checked; encrypted assertions are not offered in R2.
/// </summary>
public sealed class SamlService(
    IDbSession session,
    TenantContextAccessor context,
    IdentityProviderStore providers,
    IEntitlementService entitlements,
    SignInService signIn,
    Directory.DirectorySyncService directorySync,
    IAuditWriter audit,
    IClock clock,
    TeamBeats.Modules.Security.Contracts.ISecurityPolicy securityPolicy,
    TeamBeats.Modules.Security.Contracts.IAuthenticationAssurance assurance)
{
    public const string Protocol = "saml";
    public const string PersistentFormat = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";
    private const string Samlp = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string Saml = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string Bearer = "urn:oasis:names:tc:SAML:2.0:cm:bearer";
    private const string Success = "urn:oasis:names:tc:SAML:2.0:status:Success";
    private const int MaxResponseBytes = 256 * 1024;

    /// <summary>Attribute names AD FS and common IdPs use for the values sign-in reads.</summary>
    private static readonly string[] ObjectGuidNames = ["objectGUID", "objectguid", "http://schemas.microsoft.com/identity/claims/objectguid"];
    private static readonly string[] EmailNames = ["email", "mail", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn"];
    private static readonly string[] NameNames = ["displayName", "name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"];
    private static readonly string[] AmrNames = ["amr", "http://schemas.microsoft.com/claims/authnmethodsreferences"];

    /// <summary>The service provider's entity ID on this tenant host, which is also its metadata address.</summary>
    public static string EntityId(string host) => $"https://{host}{SamlEndpoints.MetadataPath}";

    public static string AcsUrl(string host) => $"https://{host}{SamlEndpoints.AcsPath}";

    /// <summary>SP metadata for the tenant admin to give their IdP: entity ID, the POST consumer and persistent name IDs.</summary>
    public static string Metadata(string host) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{SecurityElement(EntityId(host))}">
          <md:SPSSODescriptor AuthnRequestsSigned="false" WantAssertionsSigned="true" protocolSupportEnumeration="{Samlp}">
            <md:NameIDFormat>{PersistentFormat}</md:NameIDFormat>
            <md:AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{SecurityElement(AcsUrl(host))}" index="0" isDefault="true"/>
          </md:SPSSODescriptor>
        </md:EntityDescriptor>
        """;

    public async Task<SsoStep> StartAsync(TenantInfo tenant, IdentityProviderConfig config, string host, string? returnTo, bool stepUp, CancellationToken ct)
    {
        context.SetTenant(tenant.Id);
        if (!await entitlements.IsEnabledAsync(Capabilities.IdentitySaml, ct))
        {
            return new SsoStep(SsoProblems.PagePath(SsoProblems.NotConfigured));
        }
        var requestId = "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        var state = Random();
        var binding = Random();
        var now = clock.UtcNow;
        await session.ExecAsync("delete from identity.oidc_login_state where expires_at < @cutoff", new { cutoff = now - TimeSpan.FromHours(1) }, ct);
        // The request ID sits in the nonce column: it is what the response must answer, once.
        await session.ExecAsync(
            """
            insert into identity.oidc_login_state (tenant_id, id, state_hash, browser_binding_hash, idp_config_id, nonce, code_verifier, return_path, expires_at, step_up)
            values (@t, @id, @state, @binding, @idp, @nonce, '', @ret, @exp, @stepUp)
            """,
            new { t = tenant.Id, id = Ids.New(), state = Hash(state), binding = Hash(binding), idp = config.Id, nonce = requestId,
                ret = ReturnPaths.Sanitize(returnTo), exp = now + SsoService.StateLifetime, stepUp }, ct);

        var request = $"""<samlp:AuthnRequest xmlns:samlp="{Samlp}" xmlns:saml="{Saml}" ID="{requestId}" Version="2.0" IssueInstant="{now.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}" Destination="{SecurityElement(config.SsoUrl!)}" AssertionConsumerServiceURL="{SecurityElement(AcsUrl(host))}" ProtocolBinding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"{(stepUp ? " ForceAuthn=\"true\"" : string.Empty)}><saml:Issuer>{SecurityElement(EntityId(host))}</saml:Issuer><samlp:NameIDPolicy AllowCreate="true"/></samlp:AuthnRequest>""";
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes(request));
        }
        var separator = config.SsoUrl!.Contains('?') ? "&" : "?";
        var location = config.SsoUrl + separator + "SAMLRequest=" + Uri.EscapeDataString(Convert.ToBase64String(buffer.ToArray()))
            + "&RelayState=" + Uri.EscapeDataString(state);
        return new SsoStep(location, BindingCookie: binding, Saml: true);
    }

    public async Task<SsoStep> AcsAsync(TenantInfo tenant, string host, string? samlResponse, string? relayState, string? bindingCookie, CancellationToken ct)
    {
        context.SetTenant(tenant.Id);
        if (string.IsNullOrEmpty(relayState))
        {
            await FailAsync("state_missing", null, ct);
            return Problem(SsoProblems.Failed);
        }
        // Consume first: whatever happens next, this request can never be answered again.
        var pending = await session.QuerySingleOrDefaultAsync(
            """
            update identity.oidc_login_state set consumed_at = now()
            where state_hash = @h and consumed_at is null and expires_at > @now and code_verifier = ''
            returning idp_config_id, browser_binding_hash, nonce, return_path, step_up
            """, new { h = Hash(relayState), now = clock.UtcNow },
            r => (Idp: r.GetGuid(0), Binding: r.GetString(1), RequestId: r.GetString(2), ReturnPath: r.GetString(3), StepUp: r.GetBoolean(4)), ct);
        if (pending == default)
        {
            await FailAsync("state_invalid", null, ct);
            return Problem(SsoProblems.Failed);
        }
        if (string.IsNullOrEmpty(bindingCookie) || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Hash(bindingCookie)), Encoding.ASCII.GetBytes(pending.Binding)))
        {
            await FailAsync("browser_binding_mismatch", null, ct);
            return Problem(SsoProblems.Failed);
        }
        var config = await providers.GetAsync(pending.Idp, ct);
        if (config is not { Enabled: true, Protocol: Protocol } || !await entitlements.IsEnabledAsync(Capabilities.IdentitySaml, ct))
        {
            await FailAsync("provider_disabled", null, ct);
            return Problem(SsoProblems.NotConfigured);
        }

        SamlAssertion assertion;
        try
        {
            assertion = Verify(samlResponse, config, EntityId(host), AcsUrl(host), pending.RequestId, clock.UtcNow);
        }
        catch (SamlRejectedException rejected)
        {
            await FailAsync(rejected.Reason, config, ct);
            return Problem(SsoProblems.Failed);
        }

        var link = LinkFor(config, assertion);
        if (link.Problem is { } problem)
        {
            await FailAsync(problem, config, ct);
            return Problem(SsoProblems.Failed);
        }
        if (pending.StepUp && !(assertion.AuthnInstant is { } at && at >= clock.UtcNow - SsoService.ClockSkew))
        {
            context.SetActor(ActorType.Anonymous, null);
            await audit.WriteAsync(new AuditEntry(AuditActions.SecurityStepUpFailed, "user", null, AuditOutcome.Failure,
                Changes: new { reason = "authn_instant_stale", issuer = config.Issuer }), ct);
            return Problem(SsoProblems.Failed);
        }
        var amr = AmrNames.SelectMany(n => assertion.Attributes.GetValueOrDefault(n) ?? []).ToList();
        if (!await assurance.CheckMfaAsync(config.Kind, amr, assertion.AuthnContext, ct) && (await securityPolicy.GetAsync(ct)).RequireMfa)
        {
            context.SetActor(ActorType.Anonymous, null);
            await audit.WriteAsync(new AuditEntry(AuditActions.SecurityMfaMissing, "user", null, AuditOutcome.Denied,
                Changes: new { issuer = config.Issuer, method = "saml_" + config.Kind, amr, acr = assertion.AuthnContext }), ct);
            return Problem(SsoProblems.MfaRequired);
        }

        var email = First(assertion, EmailNames) ?? (assertion.NameId.Contains('@') ? assertion.NameId : string.Empty);
        var name = First(assertion, NameNames) ?? email;
        var jit = config.JitEnabled && !await directorySync.IsActiveAsync(ct);
        var policy = new ProviderSignInPolicy(config.Id, jit, config.JitDomains, null, config.GroupRoleMap);
        var result = await signIn.SignInAsync(tenant, link.Issuer!, link.Subject!, email, name, "saml_" + config.Kind, "admin", ct, policy);
        if (result.Outcome == SignInOutcome.Succeeded && config.LastProblem is not null)
        {
            await providers.ClearProblemAsync(config.Id, ct);
        }
        return result.Outcome switch
        {
            SignInOutcome.Succeeded => new SsoStep(pending.ReturnPath, SessionToken: result.SessionToken),
            SignInOutcome.RefusedTenantInactive => Problem(SsoProblems.TenantSuspended),
            _ => Problem(SsoProblems.NoAccess),
        };
    }

    /// <summary>
    /// The issuer and subject a verified assertion links under (FR-003): AD FS by <c>objectGUID</c> under the AD DS
    /// connection, any other IdP by a persistent name ID under its entity ID. Email or transient name IDs never link.
    /// </summary>
    public static SsoService.SubjectLink LinkFor(IdentityProviderConfig config, SamlAssertion assertion)
    {
        if (config.Kind == IdentityProviderStore.KindAdfs)
        {
            if (config.DirectoryConnectionId is not { } connection)
            {
                return new(null, null, "directory_connection_missing");
            }
            return SsoService.ObjectGuid(First(assertion, ObjectGuidNames)) is { } guid
                ? new(Directory.DirectorySyncService.OnPremIssuer(connection), guid, null)
                : new(null, null, "object_guid_missing");
        }
        return assertion.NameIdFormat == PersistentFormat && assertion.NameId.Length is > 0 and <= 256
            ? new(config.Issuer, assertion.NameId, null)
            : new(null, null, "name_id_not_persistent");
    }

    /// <summary>Checks a base64 SAML response from the POST binding and returns the one assertion it vouches for.</summary>
    public static SamlAssertion Verify(string? encoded, IdentityProviderConfig config, string audience, string recipient, string requestId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxResponseBytes * 4 / 3 + 4)
        {
            throw new SamlRejectedException("response_missing");
        }
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new SamlRejectedException("response_malformed");
        }
        var doc = Load(raw);
        var response = doc.DocumentElement;
        if (response is not { LocalName: "Response", NamespaceURI: Samlp })
        {
            throw new SamlRejectedException("response_malformed");
        }
        if (Child(Child(response, Samlp, "Status"), Samlp, "StatusCode")?.GetAttribute("Value") != Success)
        {
            throw new SamlRejectedException("idp_error");
        }
        if (response.GetAttribute("InResponseTo") != requestId)
        {
            throw new SamlRejectedException("in_response_to_mismatch");
        }
        if (response.HasAttribute("Destination") && response.GetAttribute("Destination") != recipient)
        {
            throw new SamlRejectedException("wrong_destination");
        }
        if (doc.GetElementsByTagName("EncryptedAssertion", Saml).Count > 0)
        {
            throw new SamlRejectedException("encrypted_assertion_unsupported");
        }
        var assertions = doc.GetElementsByTagName("Assertion", Saml);
        if (assertions.Count != 1 || assertions[0]!.ParentNode != response)
        {
            // Exactly one assertion, directly in the response: a second one is how signature wrapping hides itself.
            throw new SamlRejectedException("assertion_count");
        }
        var assertion = (XmlElement)assertions[0]!;

        // Trust comes from a signature over the response or over the assertion; either covers the assertion.
        var certificates = (config.Certificates ?? []).Select(c => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(c))).ToList();
        if (!IsSignedBy(response, doc, certificates) && !IsSignedBy(assertion, doc, certificates))
        {
            throw new SamlRejectedException("bad_signature");
        }

        if (Child(assertion, Saml, "Issuer")?.InnerText.Trim() != config.Issuer)
        {
            throw new SamlRejectedException("wrong_issuer");
        }
        var conditions = Child(assertion, Saml, "Conditions") ?? throw new SamlRejectedException("conditions_missing");
        if (!Within(conditions, now))
        {
            throw new SamlRejectedException("expired");
        }
        var audiences = conditions.GetElementsByTagName("Audience", Saml).Cast<XmlElement>().Select(a => a.InnerText.Trim()).ToList();
        if (!audiences.Contains(audience, StringComparer.Ordinal))
        {
            throw new SamlRejectedException("wrong_audience");
        }

        var subject = Child(assertion, Saml, "Subject") ?? throw new SamlRejectedException("subject_missing");
        var confirmed = subject.GetElementsByTagName("SubjectConfirmation", Saml).Cast<XmlElement>().Any(c =>
            c.GetAttribute("Method") == Bearer && Child(c, Saml, "SubjectConfirmationData") is { } data
            && data.GetAttribute("Recipient") == recipient
            && data.GetAttribute("InResponseTo") == requestId
            && data.HasAttribute("NotOnOrAfter") && Within(data, now));
        if (!confirmed)
        {
            throw new SamlRejectedException("subject_not_confirmed");
        }
        var nameId = Child(subject, Saml, "NameID") ?? throw new SamlRejectedException("subject_missing");

        var attributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var attribute in assertion.GetElementsByTagName("Attribute", Saml).Cast<XmlElement>())
        {
            var values = attribute.GetElementsByTagName("AttributeValue", Saml).Cast<XmlElement>().Select(v => v.InnerText.Trim()).Where(v => v.Length > 0).ToList();
            attributes[attribute.GetAttribute("Name")] = values;
        }
        var authn = Child(assertion, Saml, "AuthnStatement");
        var context = authn is null ? null : Child(Child(authn, Saml, "AuthnContext"), Saml, "AuthnContextClassRef")?.InnerText.Trim();
        DateTimeOffset? instant = authn is not null && DateTimeOffset.TryParse(authn.GetAttribute("AuthnInstant"), null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        return new SamlAssertion(config.Issuer, nameId.InnerText.Trim(), nameId.HasAttribute("Format") ? nameId.GetAttribute("Format") : null, attributes, context, instant);
    }

    /// <summary>
    /// True only when <paramref name="element"/> carries a signature, as its own child, with one reference to its own ID
    /// (an ID no other element shares), the enveloped and exclusive canonical transforms only, RSA with SHA-256 or
    /// stronger, made by one of the configured certificates. Keys in the signature itself are never trusted.
    /// </summary>
    private static bool IsSignedBy(XmlElement element, XmlDocument doc, IReadOnlyList<X509Certificate2> certificates)
    {
        var signatures = element.ChildNodes.OfType<XmlElement>().Where(e => e is { LocalName: "Signature", NamespaceURI: SignedXml.XmlDsigNamespaceUrl }).ToList();
        if (signatures.Count != 1)
        {
            return false;
        }
        var id = element.GetAttribute("ID");
        if (id.Length == 0 || doc.SelectNodes($"//*[@ID='{id.Replace("'", string.Empty)}']")?.Count != 1)
        {
            return false;
        }
        var signed = new IdSignedXml(element);
        signed.LoadXml(signatures[0]);
        if (signed.SignedInfo is not { } info || info.References.Count != 1 || ((Reference)info.References[0]!).Uri != "#" + id)
        {
            return false;
        }
        if (info.SignatureMethod is not (SignedXml.XmlDsigRSASHA256Url or SignedXml.XmlDsigRSASHA384Url or SignedXml.XmlDsigRSASHA512Url))
        {
            return false;
        }
        var transforms = ((Reference)info.References[0]!).TransformChain;
        for (var i = 0; i < transforms.Count; i++)
        {
            if (transforms[i] is not (XmlDsigEnvelopedSignatureTransform or XmlDsigExcC14NTransform))
            {
                return false;
            }
        }
        foreach (var certificate in certificates)
        {
            using var key = certificate.GetRSAPublicKey();
            if (key is not null && signed.CheckSignature(key))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Resolves the reference by the SAML <c>ID</c> attribute, and only to the element being checked.</summary>
    private sealed class IdSignedXml : SignedXml
    {
        private readonly XmlElement signedElement;

        public IdSignedXml(XmlElement element) : base(element) => signedElement = element;

        public override XmlElement? GetIdElement(XmlDocument? document, string idValue) =>
            signedElement.GetAttribute("ID") == idValue ? signedElement : null;
    }

    private static XmlDocument Load(byte[] raw)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxResponseBytes * 2 };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(raw), settings);
            doc.Load(reader);
        }
        catch (XmlException)
        {
            throw new SamlRejectedException("response_malformed");
        }
        return doc;
    }

    private static XmlElement? Child(XmlElement? parent, string ns, string name) =>
        parent?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == name && e.NamespaceURI == ns);

    private static bool Within(XmlElement element, DateTimeOffset now)
    {
        static DateTimeOffset? Read(XmlElement e, string name) =>
            e.HasAttribute(name) && DateTimeOffset.TryParse(e.GetAttribute(name), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var v) ? v : null;
        var notBefore = Read(element, "NotBefore");
        var notOnOrAfter = Read(element, "NotOnOrAfter");
        return (notBefore is null || notBefore.Value - SsoService.ClockSkew <= now) && (notOnOrAfter is null || now < notOnOrAfter.Value + SsoService.ClockSkew);
    }

    private static string? First(SamlAssertion assertion, string[] names) =>
        names.Select(n => assertion.Attributes.GetValueOrDefault(n)?.FirstOrDefault()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string SecurityElement(string value) => System.Security.SecurityElement.Escape(value);

    private static SsoStep Problem(string reason) => new(SsoProblems.PagePath(reason));

    private async Task FailAsync(string reason, IdentityProviderConfig? config, CancellationToken ct)
    {
        context.SetActor(ActorType.Anonymous, null);
        await audit.WriteAsync(new AuditEntry(AuditActions.SignInFailed, "user", null, AuditOutcome.Failure,
            Changes: new { reason, issuer = config?.Issuer, method = config is null ? "saml" : "saml_" + config.Kind }), ct);
        if (config is not null)
        {
            await providers.RecordProblemAsync(config.Id, reason, ct);
        }
    }

    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
