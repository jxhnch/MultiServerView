using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiServerView.Core.Web;

/// <summary>Pure text helpers of the web proxy (kept apart from the networking code so they can be tested).</summary>
public static class WebRewriter
{
    /// <summary>Content types whose text is rewritten (addresses of the real server -> address of the local proxy).</summary>
    public static bool IsRewritable(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var t = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return t is "text/html" or "application/xhtml+xml" or "text/css" or "application/javascript" or "text/javascript" or "application/x-javascript"
            or "application/json" or "text/json" or "text/plain" or "application/xml" or "text/xml";
    }

    public static bool IsHtml(string? contentType)
    {
        var t = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return t is "text/html" or "application/xhtml+xml";
    }

    /// <summary>Replaces every written form of the real origin (plain and JSON-escaped) with the proxy origin.</summary>
    public static string ReplaceOrigin(string text, string realOrigin, string proxyOrigin)
    {
        if (text.Length == 0) return text;
        text = text.Replace(realOrigin, proxyOrigin, StringComparison.OrdinalIgnoreCase);
        var escapedReal = realOrigin.Replace("/", "\\/");
        if (text.Contains(escapedReal, StringComparison.OrdinalIgnoreCase))
            text = text.Replace(escapedReal, proxyOrigin.Replace("/", "\\/"), StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>A redirect to the real server must stay inside the proxy.</summary>
    public static string RewriteLocation(string location, string realOrigin, string proxyOrigin)
        => location.StartsWith(realOrigin, StringComparison.OrdinalIgnoreCase) ? proxyOrigin + location[realOrigin.Length..] : location;

    /// <summary>
    /// All proxies share the host 127.0.0.1, and cookies do not know ports: in the browser every cookie of a device gets a name
    /// prefix of that device, so one device never receives the cookies of another one.
    /// </summary>
    public static string CookiePrefix(string realOrigin)
        => "msv" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(realOrigin.ToLowerInvariant())), 0, 5).ToLowerInvariant() + "_";

    /// <summary>Of the cookies the browser sent, only those of this device, under their real names.</summary>
    public static Dictionary<string, string> OwnCookies(IReadOnlyDictionary<string, string> fromBrowser, string prefix)
        => fromBrowser.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value, StringComparer.Ordinal);

    /// <summary>
    /// A cookie of the real server is stored by the browser for the proxy address: under the device's prefix, no Domain (it would
    /// not match), no Secure (the proxy speaks plain http on the loopback address) and no SameSite=None (it needs Secure).
    /// </summary>
    public static string RewriteSetCookie(string cookie, string prefix)
    {
        var parts = cookie.Split(';');
        var pair = parts[0].Trim();
        var sb = new StringBuilder(pair.Contains('=') ? prefix + pair : pair);
        for (var i = 1; i < parts.Length; i++)
        {
            var attribute = parts[i].Trim();
            var name = attribute.Split('=')[0].Trim().ToLowerInvariant();
            if (name is "domain" or "secure" or "partitioned") continue;
            if (name == "samesite" && attribute.EndsWith("none", StringComparison.OrdinalIgnoreCase)) continue;
            if (attribute.Length > 0) sb.Append("; ").Append(attribute);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pages also set cookies by script, and some (Proxmox: its login ticket) mark them "Secure". WebKit, the browser engine on
    /// Linux, silently drops such a cookie on the proxy's plain http address, so signing in never works. This script runs before
    /// the page's own scripts and makes document.cookie treat cookies like <see cref="RewriteSetCookie"/> does.
    /// </summary>
    public static string BuildCookieScript(string prefix)
        => "<script>(function(){var P=" + ScriptString(prefix) + CookieScriptBody + "})();</script>";

    private const string CookieScriptBody = """
;var D=Object.getOwnPropertyDescriptor(Document.prototype,'cookie');if(!D||!D.set||window.__msvCookies)return;window.__msvCookies=1;
Object.defineProperty(document,'cookie',{configurable:true,enumerable:true,
get:function(){return String(D.get.call(document)).split('; ').filter(function(c){return c.indexOf(P)===0;}).map(function(c){return c.slice(P.length);}).join('; ');},
set:function(value){var parts=String(value).split(';'),pair=parts.shift().trim();if(pair.indexOf('=')<0)return;
var kept=parts.filter(function(a){var t=a.trim().toLowerCase(),n=t.split('=')[0].trim();return n!=='secure'&&n!=='domain'&&n!=='partitioned'&&!(n==='samesite'&&/none$/.test(t));});
D.set.call(document,[P+pair].concat(kept).join(';'));}});
""";

    /// <summary>JSON string literal that is safe inside an inline script element.</summary>
    public static string ScriptString(string value)
        => JsonSerializer.Serialize(value ?? "").Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");

    /// <summary>
    /// Small script that is added to every page of the web host: it waits for a visible password field, fills in the saved
    /// user name and password and sends the form once. Without saved credentials nothing is added.
    /// </summary>
    public static string BuildAutoLoginScript(string username, string password)
    {
        var js = BuildAutoLoginJs(username, password);
        return js.Length == 0 ? "" : "<script>" + js + "</script>";
    }

    /// <summary>The same as plain JavaScript. With <paramref name="onlyOrigin"/> it does nothing on pages of any other origin.</summary>
    public static string BuildAutoLoginJs(string username, string password, string onlyOrigin = "")
    {
        if (username.Length == 0 && password.Length == 0) return "";
        var guard = onlyOrigin.Length == 0 ? "" : "if(location.origin!==" + ScriptString(onlyOrigin) + ")return;if(window.__msvLogin)return;window.__msvLogin=1;";
        return "(function(){" + guard + "var U=" + ScriptString(username) + ",P=" + ScriptString(password) + AutoLoginBody + "})();";
    }

    private const string AutoLoginBody = """
,K='msv-autologin';
function vis(e){if(!e||e.disabled)return false;var r=e.getBoundingClientRect();if(r.width<2||r.height<2)return false;var s=getComputedStyle(e);return s.visibility!=='hidden'&&s.display!=='none';}
function put(e,v){try{var d=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value');d.set.call(e,v);}catch(x){e.value=v;}e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));}
function submitted(){try{return !!sessionStorage.getItem(K);}catch(x){return false;}}
function mark(){try{sessionStorage.setItem(K,String(Date.now()));}catch(x){}}
function fill(){
 var pws=[].slice.call(document.querySelectorAll('input[type=password]')).filter(vis);
 if(!pws.length)return false;
 if(pws.length!==1)return true;
 var p=pws[0];if(p.getAttribute('data-msv')==='1')return true;
 var scope=p.form||document;
 var u=null;[].slice.call(scope.querySelectorAll('input')).forEach(function(i){if(i!==p&&vis(i)&&/^(text|email|tel|)$/i.test(i.type)&&!i.readOnly&&(i.compareDocumentPosition(p)&4))u=i;});
 p.setAttribute('data-msv','1');
 if(u&&U&&!u.value)put(u,U);
 if(P&&!p.value)put(p,P);
 if(!submitted()){
  mark();
  setTimeout(function(){
   var f=p.form,b=null;
   if(f){b=f.querySelector('button[type=submit],input[type=submit]')||f.querySelector('button:not([type=button]):not([type=reset])');}
   if(b&&!b.disabled)b.click();
   else if(f&&f.requestSubmit)f.requestSubmit();
   else{var o={key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true};p.dispatchEvent(new KeyboardEvent('keydown',o));p.dispatchEvent(new KeyboardEvent('keyup',o));}
  },350);
 }
 return true;
}
function go(){try{fill();}catch(x){}}
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',go);else go();
var n=0,t=setInterval(function(){go();if(++n>40)clearInterval(t);},500);
try{new MutationObserver(function(){go();}).observe(document.documentElement,{childList:true,subtree:true});}catch(x){}
""";

    /// <summary>Puts the script into the page (right after the opening head tag, so it runs first; at the very start if there is no head).</summary>
    public static string InjectIntoHtml(string html, string snippet)
    {
        if (snippet.Length == 0) return html;
        var head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (head >= 0)
        {
            var close = html.IndexOf('>', head);
            if (close >= 0) return html.Insert(close + 1, snippet);
        }
        return snippet + html;
    }
}
