using System;
using System.Collections.Generic;
using System.Linq;

namespace Voidstrap.UI.Elements.Overlay;

// Ad and tracker blocking by address. The browser engine checks these patterns itself, so pages that load
// nothing from these hosts cost nothing extra, and a blocked request never leaves the computer.
public static class BrowserBlocklist
{
    // Ad networks, ad exchanges, pop-up networks and analytics or session-recording trackers
    private static readonly string[] Hosts =
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "google-analytics.com", "googletagmanager.com",
        "googletagservices.com", "adservice.google.com", "app-measurement.com",
        "adnxs.com", "adsrvr.org", "amazon-adsystem.com", "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "scorecardresearch.com", "quantserve.com", "quantcount.com", "moatads.com", "pubmatic.com", "rubiconproject.com",
        "openx.net", "casalemedia.com", "indexww.com", "advertising.com", "adform.net", "smartadserver.com", "yieldmo.com",
        "sharethrough.com", "teads.tv", "33across.com", "media.net", "bidswitch.net", "contextweb.com", "lijit.com",
        "sovrn.com", "adcolony.com", "inmobi.com", "mopub.com", "adroll.com", "adsafeprotected.com", "doubleverify.com",
        "serving-sys.com", "zedo.com", "revcontent.com", "mgid.com", "propellerads.com", "popads.net", "popcash.net",
        "exoclick.com", "juicyads.com", "trafficjunky.net", "adsterra.com", "hilltopads.net", "onclickads.net",
        "adskeeper.com", "zergnet.com", "undertone.com", "gumgum.com", "triplelift.com", "3lift.com", "spotxchange.com",
        "springserve.com", "smaato.net", "adtechus.com", "yieldlab.net", "districtm.io", "emxdgt.com", "rhythmone.com",
        "chartbeat.com", "chartbeat.net", "hotjar.com", "hotjar.io", "mixpanel.com", "fullstory.com", "mouseflow.com",
        "crazyegg.com", "clarity.ms", "luckyorange.com", "inspectlet.com", "kissmetrics.com", "heapanalytics.com",
        "bat.bing.com", "px.ads.linkedin.com", "ads.linkedin.com", "analytics.tiktok.com", "ads-api.tiktok.com",
        "an.facebook.com", "pixel.facebook.com", "ads.twitter.com", "static.ads-twitter.com", "analytics.twitter.com",
        "events.redditmedia.com", "alb.reddit.com", "ads.pinterest.com", "ct.pinterest.com", "mc.yandex.ru", "an.yandex.ru",
        "adfox.ru", "cdn.segment.com", "api.segment.io", "omtrdc.net", "demdex.net", "everesttech.net", "krxd.net",
        "bluekai.com", "exelator.com", "agkn.com", "rlcdn.com", "tapad.com", "mathtag.com", "turn.com", "simpli.fi",
        "adition.com", "branch.io", "app.link", "onesignal.com", "pushwoosh.com"
    };

    // Ad and tracking addresses on sites that are otherwise wanted
    private static readonly string[] Paths =
    {
        "*://*.youtube.com/pagead/*",
        "*://*.youtube.com/api/stats/ads*",
        "*://*.youtube.com/ptracking*",
        "*://*.google.com/pagead/*",
        "*://*.reddit.com/api/ad*"
    };

    private static readonly HashSet<string> HostSet = new(Hosts, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Patterns
        => Hosts.SelectMany(host => new[] { $"*://{host}/*", $"*://*.{host}/*" }).Concat(Paths);

    // True when the host is, or is under, a blocked host
    public static bool IsBlockedHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
            return false;
        string current = host;
        while (true)
        {
            if (HostSet.Contains(current))
                return true;
            int dot = current.IndexOf('.');
            if (dot < 0 || dot == current.Length - 1)
                return false;
            current = current[(dot + 1)..];
        }
    }
}
