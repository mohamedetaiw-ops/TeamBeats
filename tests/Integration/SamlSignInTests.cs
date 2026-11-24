using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 003 R2 (US4, US5; IAM-006): SAML 2.0 sign-in. The test plays the IdP with a certificate it makes. A response
/// counts only when signed by the configured certificate over the one element it references, for this request, this
/// audience and this consumer, in time, once; persistent name IDs link for a generic IdP and objectGUID for AD FS. The
/// tenant boundary, the entitlement and the admin's view of a refused response are included.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SamlSignInTests(TenantWorld world)
{
    private const string Provider = "/api/v1/tenant/identity-provider";
    private const string Persistent = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";
    private static readonly X509Certificate2 IdpCert = MakeCertificate("CN=Fictional IdP");
    private static readonly X509Certificate2 OtherCert = MakeCertificate("CN=Someone Else");

    private static X509Certificate2 MakeCertificate(string subject, bool expired = false)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        return expired ? request.CreateSelfSigned(now.AddDays(-10), now.AddDays(-1)) : request.CreateSelfSigned(now.AddDays(-1), now.AddDays(365));
    }

    private static string Der(X509Certificate2 cert) => Convert.ToBase64String(cert.RawData);

    private static string IdpIssuer(TestTenant t) => $"https://idp.{t.Key}.example/saml";

    private static string Host(TestTenant t) => $"{t.Key}.{ApiFactory.BaseDomain}";

    private async Task<TestTenant> TenantAsync(bool jit = true)
    {
        var t = await world.CreateTenantAsync("sm-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var saved = await t["admin"].Client.PutAsJsonAsync(Provider, new
        {
            protocol = "saml", kind = "generic", issuer = IdpIssuer(t), ssoUrl = $"https://idp.{t.Key}.example/sso",
            certificates = new[] { "-----BEGIN CERTIFICATE-----\n" + Der(IdpCert) + "\n-----END CERTIFICATE-----" },
            jitEnabled = jit, jitDomains = jit ? new[] { $"{t.Key}.example" } : [],
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return t;
    }

    private sealed record Pending(string RequestId, string RelayState, string Binding, XmlDocument Request);

    private async Task<Pending> StartAsync(TestTenant t, string? ssoUrl = null)
    {
        var start = await world.Factory.TenantClient(t.Key).GetAsync("/api/v1/auth/signin/sso");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = new Uri(start.Headers.Location!.ToString());
        Assert.StartsWith((ssoUrl ?? $"https://idp.{t.Key}.example/sso") + "?SAMLRequest=", location.ToString(), StringComparison.Ordinal);
        var query = FakeIdentityProvider.ParseQuery(location.Query);
        using var inflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(query["SAMLRequest"])), CompressionMode.Decompress);
        var request = new XmlDocument();
        request.Load(inflate);
        var cookie = start.Headers.GetValues("Set-Cookie").First(v => v.StartsWith("tb_saml=", StringComparison.Ordinal));
        Assert.Contains("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth/signin/saml/acs", cookie, StringComparison.OrdinalIgnoreCase);
        return new Pending(request.DocumentElement!.GetAttribute("ID"), query["RelayState"], cookie.Split(';')[0]["tb_saml=".Length..], request);
    }

    private sealed record Answer
    {
        public string NameId { get; init; } = "persistent-" + Guid.NewGuid().ToString("N");
        public string Format { get; init; } = Persistent;
        public string? Email { get; init; }
        public string? InResponseTo { get; init; }
        public string? Audience { get; init; }
        public string? Recipient { get; init; }
        public string? Issuer { get; init; }
        public TimeSpan Age { get; init; } = TimeSpan.Zero;
        public bool SignResponse { get; init; } = true;
        public bool SignAssertion { get; init; }
        public X509Certificate2 Signer { get; init; } = IdpCert;
        public Dictionary<string, string> Attributes { get; init; } = [];
    }

    /// <summary>The IdP's response to a pending request, signed as asked.</summary>
    private static XmlDocument Respond(TestTenant t, Pending pending, Answer a)
    {
        var now = DateTime.UtcNow - a.Age;
        string Time(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var acs = $"https://{Host(t)}/api/v1/auth/signin/saml/acs";
        var irt = a.InResponseTo ?? pending.RequestId;
        var attributes = string.Concat(a.Attributes.Append(new("email", a.Email ?? $"sam@{t.Key}.example")).Select(kv =>
            $"<saml:Attribute Name=\"{kv.Key}\"><saml:AttributeValue>{kv.Value}</saml:AttributeValue></saml:Attribute>"));
        var xml = $"""
            <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="_r{Guid.NewGuid():N}" Version="2.0" IssueInstant="{Time(now)}" Destination="{acs}" InResponseTo="{irt}"><saml:Issuer>{a.Issuer ?? IdpIssuer(t)}</saml:Issuer><samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status><saml:Assertion ID="_a{Guid.NewGuid():N}" Version="2.0" IssueInstant="{Time(now)}"><saml:Issuer>{a.Issuer ?? IdpIssuer(t)}</saml:Issuer><saml:Subject><saml:NameID Format="{a.Format}">{a.NameId}</saml:NameID><saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer"><saml:SubjectConfirmationData InResponseTo="{irt}" Recipient="{a.Recipient ?? acs}" NotOnOrAfter="{Time(now.AddMinutes(5))}"/></saml:SubjectConfirmation></saml:Subject><saml:Conditions NotBefore="{Time(now.AddMinutes(-1))}" NotOnOrAfter="{Time(now.AddMinutes(5))}"><saml:AudienceRestriction><saml:Audience>{a.Audience ?? $"https://{Host(t)}/api/v1/auth/signin/saml/metadata"}</saml:Audience></saml:AudienceRestriction></saml:Conditions><saml:AuthnStatement AuthnInstant="{Time(now)}"><saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport</saml:AuthnContextClassRef></saml:AuthnContext></saml:AuthnStatement><saml:AttributeStatement>{attributes}</saml:AttributeStatement></saml:Assertion></samlp:Response>
            """;
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml.Trim());
        if (a.SignAssertion)
        {
            Sign(doc, (XmlElement)doc.GetElementsByTagName("Assertion", "urn:oasis:names:tc:SAML:2.0:assertion")[0]!, a.Signer);
        }
        if (a.SignResponse)
        {
            Sign(doc, doc.DocumentElement!, a.Signer);
        }
        return doc;
    }

    private static void Sign(XmlDocument doc, XmlElement element, X509Certificate2 cert)
    {
        var signed = new SignedXml(element) { SigningKey = cert.GetRSAPrivateKey() };
        signed.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        signed.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        var reference = new Reference("#" + element.GetAttribute("ID")) { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(reference);
        signed.KeyInfo = new KeyInfo();
        signed.KeyInfo.AddClause(new KeyInfoX509Data(cert));
        signed.ComputeSignature();
        // The signature goes right after the issuer, as the schema asks.
        element.InsertAfter(doc.ImportNode(signed.GetXml(), true), element.FirstChild);
    }

    private sealed record Landed(HttpStatusCode Status, string Location, string? Session);

    private async Task<Landed> PostAsync(TestTenant t, Pending pending, XmlDocument response, string? tenantKey = null)
    {
        var client = world.Factory.TenantClient(tenantKey ?? t.Key);
        client.DefaultRequestHeaders.Add("Cookie", $"tb_saml={pending.Binding}");
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(response.OuterXml)),
            ["RelayState"] = pending.RelayState,
        });
        var landed = await client.PostAsync("/api/v1/auth/signin/saml/acs", body);
        var session = landed.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("tb_session=", StringComparison.Ordinal) && v.Length > "tb_session=".Length)
            : null;
        return new Landed(landed.StatusCode, landed.Headers.Location?.ToString() ?? string.Empty, session);
    }

    private async Task<Landed> SignInAsync(TestTenant t, Answer answer)
    {
        var pending = await StartAsync(t);
        return await PostAsync(t, pending, Respond(t, pending, answer));
    }

    private Task<long> FailuresAsync(TestTenant t, string reason) => world.Database.TenantScalarAsync<long>(t.Id,
        $"select count(*) from audit.audit_event where action = '{AuditActions.SignInFailed}' and changes->>'reason' = @r", ("r", reason));

    private const string Refused = "/signin/problem?reason=failed";

    [Fact]
    public async Task The_request_names_this_service_provider_and_a_signed_persistent_answer_signs_in_and_links_on_name_id()
    {
        var t = await TenantAsync();
        var pending = await StartAsync(t);
        Assert.Equal($"https://{Host(t)}/api/v1/auth/signin/saml/acs", pending.Request.DocumentElement!.GetAttribute("AssertionConsumerServiceURL"));
        Assert.Equal($"https://{Host(t)}/api/v1/auth/signin/saml/metadata", pending.Request.GetElementsByTagName("Issuer", "urn:oasis:names:tc:SAML:2.0:assertion")[0]!.InnerText);

        var answer = new Answer { Email = $"sam@{t.Key}.example" };
        var first = await PostAsync(t, pending, Respond(t, pending, answer));
        Assert.Equal((HttpStatusCode.SeeOther, "/"), (first.Status, first.Location));
        Assert.NotNull(first.Session);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from identity.external_identity where issuer = @i and subject = @s", ("i", IdpIssuer(t)), ("s", answer.NameId)));

        // Signed assertion only, a new email: the same person by name ID.
        var second = await SignInAsync(t, answer with { SignResponse = false, SignAssertion = true, Email = $"sam.new@{t.Key}.example" });
        Assert.Equal("/", second.Location);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from identity.external_identity where subject = @s", ("s", answer.NameId)));
        Assert.Equal("saml_generic", await world.Database.TenantScalarAsync<string>(t.Id,
            $"select changes->>'method' from audit.audit_event where action = '{AuditActions.SignInSucceeded}' order by occurred_at desc limit 1"));
    }

    [Fact]
    public async Task Unsigned_foreign_signed_and_tampered_responses_are_refused_and_the_admin_sees_why()
    {
        var t = await TenantAsync();
        Assert.Equal(Refused, (await SignInAsync(t, new Answer { SignResponse = false })).Location);
        Assert.Equal(Refused, (await SignInAsync(t, new Answer { Signer = OtherCert })).Location);

        var pending = await StartAsync(t);
        var doc = Respond(t, pending, new Answer());
        doc.GetElementsByTagName("NameID", "urn:oasis:names:tc:SAML:2.0:assertion")[0]!.InnerText = "someone-else";
        Assert.Equal(Refused, (await PostAsync(t, pending, doc)).Location);
        Assert.Equal(3L, await FailuresAsync(t, "bad_signature"));
        Assert.Equal("bad_signature", (await t["admin"].Client.GetFromJsonAsync<JsonElement>(Provider)).GetProperty("lastProblem").GetString());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from identity.external_identity where issuer = @i", ("i", IdpIssuer(t))));
    }

    [Fact]
    public async Task A_second_assertion_beside_a_signed_one_is_signature_wrapping_and_is_refused()
    {
        var t = await TenantAsync();
        var pending = await StartAsync(t);
        var doc = Respond(t, pending, new Answer { SignResponse = false, SignAssertion = true });
        // The attacker keeps the signed assertion and adds their own unsigned one in front of it.
        var signed = (XmlElement)doc.GetElementsByTagName("Assertion", "urn:oasis:names:tc:SAML:2.0:assertion")[0]!;
        var evil = (XmlElement)signed.CloneNode(true);
        evil.SetAttribute("ID", "_evil");
        evil.RemoveChild(evil.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        evil.GetElementsByTagName("NameID", "urn:oasis:names:tc:SAML:2.0:assertion")[0]!.InnerText = "admin-of-everything";
        doc.DocumentElement!.InsertBefore(evil, signed);
        Assert.Equal(Refused, (await PostAsync(t, pending, doc)).Location);
        Assert.Equal(1L, await FailuresAsync(t, "assertion_count"));
    }

    [Theory]
    [InlineData("audience", "wrong_audience")]
    [InlineData("recipient", "subject_not_confirmed")]
    [InlineData("expired", "expired")]
    [InlineData("in_response_to", "in_response_to_mismatch")]
    [InlineData("issuer", "wrong_issuer")]
    [InlineData("transient", "name_id_not_persistent")]
    public async Task An_answer_for_another_audience_consumer_time_request_issuer_or_with_a_transient_id_is_refused(string change, string reason)
    {
        var t = await TenantAsync();
        var answer = change switch
        {
            "audience" => new Answer { Audience = "https://other.example/sp" },
            "recipient" => new Answer { Recipient = "https://other.example/acs" },
            "expired" => new Answer { Age = TimeSpan.FromMinutes(30) },
            "in_response_to" => new Answer { InResponseTo = "_someone-elses-request" },
            "issuer" => new Answer { Issuer = "https://idp.other.example/saml" },
            _ => new Answer { Format = "urn:oasis:names:tc:SAML:2.0:nameid-format:transient" },
        };
        Assert.Equal(Refused, (await SignInAsync(t, answer)).Location);
        Assert.Equal(1L, await FailuresAsync(t, reason));
    }

    [Fact]
    public async Task A_response_is_used_once_and_only_on_the_tenant_that_asked()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var pending = await StartAsync(a);
        var doc = Respond(a, pending, new Answer());
        // Tenant B never sees tenant A's pending request.
        Assert.Equal(Refused, (await PostAsync(a, pending, doc, b.Key)).Location);
        Assert.Equal(1L, await FailuresAsync(b, "state_invalid"));
        Assert.Equal("/", (await PostAsync(a, pending, doc)).Location);
        // Replayed: the request it answers is used up.
        Assert.Equal(Refused, (await PostAsync(a, pending, doc)).Location);
        Assert.Equal(1L, await FailuresAsync(a, "state_invalid"));
    }

    [Fact]
    public async Task Without_the_saml_capability_sign_in_does_not_start()
    {
        var t = await TenantAsync();
        await world.SetCapabilityAsync(t, Capabilities.IdentitySaml, new { enabled = false });
        var start = await world.Factory.TenantClient(t.Key).GetAsync("/api/v1/auth/signin/sso");
        Assert.Equal("/signin/problem?reason=not_configured", start.Headers.Location!.ToString());
        Assert.False((await world.Factory.TenantClient(t.Key).GetFromJsonAsync<JsonElement>("/api/v1/auth/signin/options")).GetProperty("sso").GetBoolean());
    }

    [Fact]
    public async Task Metadata_names_the_entity_and_consumer_of_this_tenant_host()
    {
        var t = await TenantAsync();
        var metadata = await world.Factory.TenantClient(t.Key).GetStringAsync("/api/v1/auth/signin/saml/metadata");
        Assert.Contains($"entityID=\"https://{Host(t)}/api/v1/auth/signin/saml/metadata\"", metadata, StringComparison.Ordinal);
        Assert.Contains($"Location=\"https://{Host(t)}/api/v1/auth/signin/saml/acs\"", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saml_provider_needs_an_https_sso_address_and_a_current_rsa_certificate_and_google_stays_on_oidc()
    {
        var t = await world.CreateTenantAsync("sv-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        async Task Invalid(object body, string field, string key)
        {
            var response = await t["admin"].Client.PutAsJsonAsync(Provider, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
            Assert.Contains(key, errors.GetProperty(field).EnumerateArray().Select(e => e.GetProperty("key").GetString()));
        }
        var cert = new[] { Der(IdpCert) };
        await Invalid(new { protocol = "saml", kind = "google", issuer = "https://idp.example/saml", ssoUrl = "https://idp.example/sso", certificates = cert }, "kind", "validation.idp.kind_unsupported");
        await Invalid(new { protocol = "saml", kind = "generic", issuer = "https://idp.example/saml", ssoUrl = "http://idp.example/sso", certificates = cert }, "ssoUrl", "validation.idp.sso_url_invalid");
        await Invalid(new { protocol = "saml", kind = "generic", issuer = "https://idp.example/saml", ssoUrl = "https://idp.example/sso", certificates = new[] { "bm90IGEgY2VydA==" } },
            "certificates", "validation.idp.certificate_invalid");
        using var old = MakeCertificate("CN=Old", expired: true);
        await Invalid(new { protocol = "saml", kind = "generic", issuer = "https://idp.example/saml", ssoUrl = "https://idp.example/sso", certificates = new[] { Der(old) } },
            "certificates", "validation.idp.certificate_expired");
        await Invalid(new { protocol = "saml", kind = "generic", issuer = "not an id", ssoUrl = "https://idp.example/sso", certificates = cert }, "issuer", "validation.idp.issuer_invalid");
    }

    [Fact]
    public async Task Adfs_over_saml_links_the_object_guid_attribute_to_the_synced_user_and_refuses_a_response_without_it()
    {
        var ad = await OnPremAd.SetupAsync(world, "workplace_plus");
        var t = ad.Tenant;
        var alice = Guid.NewGuid();
        await ad.SyncAsync(world, (alice.ToString("D"), "Alice"));
        var adfs = $"http://adfs.{t.Key}.example/adfs/services/trust";
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync(Provider, new
        {
            protocol = "saml", kind = "adfs", issuer = adfs, ssoUrl = $"https://adfs.{t.Key}.example/adfs/ls/", certificates = new[] { Der(IdpCert) },
            directoryConnectionId = ad.Connection,
        })).StatusCode);

        var sso = $"https://adfs.{t.Key}.example/adfs/ls/";
        var missing = await StartAsync(t, sso);
        Assert.Equal(Refused, (await PostAsync(t, missing, Respond(t, missing, new Answer { Issuer = adfs, Format = "urn:oasis:names:tc:SAML:2.0:nameid-format:transient" }))).Location);
        Assert.Equal(1L, await FailuresAsync(t, "object_guid_missing"));

        var pending = await StartAsync(t, sso);
        var answer = new Answer
        {
            Issuer = adfs, Format = "urn:oasis:names:tc:SAML:2.0:nameid-format:transient",
            Attributes = new() { ["http://schemas.microsoft.com/identity/claims/objectguid"] = Convert.ToBase64String(alice.ToByteArray()) },
        };
        Assert.Equal("/", (await PostAsync(t, pending, Respond(t, pending, answer))).Location);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from identity.external_identity where issuer = @i and subject = @s", ("i", DirectorySyncService.OnPremIssuer(ad.Connection)), ("s", alice.ToString("D"))));
    }
}
