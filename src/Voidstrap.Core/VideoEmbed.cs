using System;
using System.Globalization;

namespace Voidstrap.Core;

public sealed class VideoEmbed
{
    private const int IdLength = 11;
    private const int MaxStartSeconds = 86400;

    private VideoEmbed(string id, int startSeconds)
    {
        Id = id;
        StartSeconds = startSeconds;
    }

    public string Id { get; }

    public int StartSeconds { get; }

    public const string VirtualOrigin = "https://voidstrap.video";

    public string EmbedUrl
    {
        get
        {
            string start = StartSeconds > 0
                ? "&start=" + StartSeconds.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            return "https://www.youtube-nocookie.com/embed/" + Id
                + "?autoplay=0&rel=0&controls=0&fs=0&disablekb=1&modestbranding=1&playsinline=1&iv_load_policy=3&cc_load_policy=0" + start;
        }
    }

    public string EmbedUrlFor(string origin)
    {
        return EmbedUrl + "&origin=" + Uri.EscapeDataString(origin);
    }

    public string BuildPlayerHtml(string origin)
    {
        return PlayerTemplate
            .Replace("__ID__", Id)
            .Replace("__START__", StartSeconds.ToString(CultureInfo.InvariantCulture))
            .Replace("__ORIGIN__", origin);
    }

    private const string PlayerTemplate = @"<!doctype html>
<html><head><meta charset=""utf-8""><meta name=""referrer"" content=""origin"">
<style>
html,body{margin:0;padding:0;width:100%;height:100%;background:transparent;overflow:hidden;
 -webkit-user-select:none;user-select:none}
#wrap{position:relative;width:100%;height:100%;border-radius:10px;overflow:hidden;background:transparent}
#player,#player iframe{position:absolute;top:0;left:0;width:100%;height:100%;border:0}
#veil{position:absolute;top:0;left:0;right:0;bottom:0;background:transparent;cursor:pointer}
</style></head><body>
<div id=""wrap""><div id=""player""></div><div id=""veil""></div></div>
<script>
var P=null,ready=false;
var veil=document.getElementById('veil');
function noCaptions(){if(!P||!P.unloadModule)return;
 try{P.unloadModule('captions');}catch(e){}
 try{P.unloadModule('cc');}catch(e){}}
function toggle(){if(!ready||!P)return;
 if(P.getPlayerState()===1){P.pauseVideo();}else{P.playVideo();}}
veil.addEventListener('click',toggle);
function onYouTubeIframeAPIReady(){
 P=new YT.Player('player',{videoId:'__ID__',host:'https://www.youtube-nocookie.com',
  playerVars:{autoplay:0,controls:0,rel:0,fs:0,disablekb:1,modestbranding:1,playsinline:1,
   iv_load_policy:3,cc_load_policy:0,start:__START__,origin:'__ORIGIN__'},
  events:{
   onReady:function(){
    ready=true;
    var f=document.querySelector('#player');
    if(f){f.removeAttribute('allowfullscreen');f.setAttribute('allow','autoplay; encrypted-media');}
    noCaptions();setTimeout(noCaptions,1500);setTimeout(noCaptions,4000);},
   onStateChange:function(e){
    if(e.data===1)noCaptions();
    if(e.data===0){P.seekTo(__START__,true);P.pauseVideo();}}}});
}
</script>
<script src=""https://www.youtube.com/iframe_api"">
</script>
</body></html>";

    public string WatchUrl
    {
        get
        {
            string start = StartSeconds > 0
                ? "&t=" + StartSeconds.ToString(CultureInfo.InvariantCulture) + "s"
                : string.Empty;
            return "https://www.youtube.com/watch?v=" + Id + start;
        }
    }

    public static bool TryParse(string? url, out VideoEmbed? embed)
    {
        embed = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host.Substring(4)
            : uri.Host;

        string? id = null;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            id = uri.AbsolutePath.Trim('/');
        }
        else if (host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            string path = uri.AbsolutePath;
            if (path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase))
                id = path.Substring(7).Trim('/');
            else if (path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
                id = path.Substring(8).Trim('/');
            else
                id = ReadQuery(uri.Query, "v");
        }

        if (!IsValidId(id))
            return false;

        embed = new VideoEmbed(id!, ParseStart(ReadQuery(uri.Query, "t") ?? ReadQuery(uri.Query, "start")));
        return true;
    }

    private static bool IsValidId(string? id)
    {
        if (id is null || id.Length != IdLength)
            return false;
        foreach (char c in id)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok)
                return false;
        }
        return true;
    }

    private static string? ReadQuery(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;
        string trimmed = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        foreach (string pair in trimmed.Split('&'))
        {
            if (pair.Length == 0)
                continue;
            int split = pair.IndexOf('=');
            if (split <= 0)
                continue;
            if (!pair.AsSpan(0, split).Equals(key.AsSpan(), StringComparison.OrdinalIgnoreCase))
                continue;
            string value = pair.Substring(split + 1);
            return Uri.UnescapeDataString(value);
        }
        return null;
    }

    private static int ParseStart(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return 0;

        string value = raw.Trim();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int plain))
            return Clamp(plain);

        int total = 0;
        int current = 0;
        bool sawDigit = false;
        bool sawUnit = false;
        foreach (char c in value)
        {
            if (c >= '0' && c <= '9')
            {
                if (current > MaxStartSeconds)
                    return 0;
                current = (current * 10) + (c - '0');
                sawDigit = true;
                continue;
            }
            if (!sawDigit)
                return 0;
            switch (char.ToLowerInvariant(c))
            {
                case 'h':
                    total += current * 3600;
                    break;
                case 'm':
                    total += current * 60;
                    break;
                case 's':
                    total += current;
                    break;
                default:
                    return 0;
            }
            current = 0;
            sawDigit = false;
            sawUnit = true;
        }
        if (sawDigit)
            total += current;
        return sawUnit || total > 0 ? Clamp(total) : 0;
    }

    private static int Clamp(int seconds)
    {
        if (seconds < 0)
            return 0;
        return seconds > MaxStartSeconds ? MaxStartSeconds : seconds;
    }
}
