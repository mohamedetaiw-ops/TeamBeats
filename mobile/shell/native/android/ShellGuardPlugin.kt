package app.teambeats.shell

import android.content.Intent
import android.net.Uri
import com.getcapacitor.JSObject
import com.getcapacitor.Plugin
import com.getcapacitor.PluginCall
import com.getcapacitor.PluginMethod
import com.getcapacitor.annotation.CapacitorPlugin

/**
 * MOB-021 (ADR-0023): only https on the workspace host, its sign-in hosts and the start page load in the app; every
 * other address opens in the system browser. The same rule as route() in src/rules.ts, kept in step by review.
 */
@CapacitorPlugin(name = "ShellGuard")
class ShellGuardPlugin : Plugin() {
    @Volatile private var workspace: String? = null
    @Volatile private var signInHosts: Set<String> = emptySet()
    @Volatile private var start: Uri? = null
    @Volatile private var locked = false

    @PluginMethod
    fun configure(call: PluginCall) {
        workspace = call.getString("workspace")?.lowercase()
        signInHosts = (call.getArray("signInHosts")?.toList<String>() ?: emptyList()).map { it.lowercase() }.toSet()
        start = call.getString("startUrl")?.let(Uri::parse)
        locked = call.getBoolean("locked") ?: false
        call.resolve(JSObject())
    }

    override fun shouldOverrideLoad(url: Uri): Boolean? {
        if (url.scheme == "https" && url.host == "localhost") return null // The shell's own start page.
        if (inApp(url)) return false
        bridge.activity.startActivity(Intent(Intent.ACTION_VIEW, url).addCategory(Intent.CATEGORY_BROWSABLE))
        return true
    }

    private fun inApp(url: Uri): Boolean {
        if (url.scheme != "https" || url.port != -1 || url.userInfo != null) return false
        val host = url.host?.lowercase() ?: return false
        val current = workspace
        if (current != null && (host == current || host in signInHosts)) return true
        val home = start ?: return false
        return !locked && host == home.host && (url.path ?: "").startsWith(home.path ?: "/")
    }
}
