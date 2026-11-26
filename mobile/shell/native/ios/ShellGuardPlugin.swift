import Capacitor
import Foundation
import UIKit
import WebKit

/// MOB-021 (ADR-0023): only https on the workspace host, its sign-in hosts and the start page load in the app; every
/// other address opens in the system browser. The same rule as route() in src/rules.ts, kept in step by review.
@objc(ShellGuardPlugin)
public class ShellGuardPlugin: CAPPlugin, CAPBridgedPlugin {
    public let identifier = "ShellGuardPlugin"
    public let jsName = "ShellGuard"
    public let pluginMethods: [CAPPluginMethod] = [CAPPluginMethod(name: "configure", returnType: CAPPluginReturnPromise)]

    private var workspace: String?
    private var signInHosts: Set<String> = []
    private var start: URL?
    private var locked = false

    @objc func configure(_ call: CAPPluginCall) {
        workspace = call.getString("workspace")?.lowercased()
        signInHosts = Set((call.getArray("signInHosts", String.self) ?? []).map { $0.lowercased() })
        start = call.getString("startUrl").flatMap(URL.init(string:))
        locked = call.getBool("locked") ?? false
        call.resolve()
    }

    public override func shouldOverrideLoad(_ navigationAction: WKNavigationAction) -> NSNumber? {
        guard let url = navigationAction.request.url else { return nil }
        if url.scheme == "capacitor" { return nil } // The shell's own start page.
        if inApp(url) { return false }
        DispatchQueue.main.async { UIApplication.shared.open(url) }
        return true
    }

    private func inApp(_ url: URL) -> Bool {
        guard url.scheme == "https", url.port == nil, url.user == nil, let host = url.host?.lowercased() else { return false }
        if let current = workspace, host == current || signInHosts.contains(host) { return true }
        guard !locked, let home = start else { return false }
        return host == home.host && url.path.hasPrefix(home.path)
    }
}
