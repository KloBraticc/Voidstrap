using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Integrations;

namespace Voidstrap.Utility
{
    public static class WebsiteGeoSync
    {
        private const int MaxEntriesPerPush = 100;

        private static int _pushScheduled;

        private static DateTime _lastPushUtc = DateTime.MinValue;

        private static readonly TimeSpan PushInterval = TimeSpan.FromMinutes(10);

		private static readonly CancellationTokenSource LifetimeCancellation = new();

        public static async Task PullAsync()
        {
            try
            {
                await ServerFetchStore.RefreshFromRemoteAsync(App.WebsiteBaseUrl + "/api/geoshare").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WebsiteGeoSync::Pull", ex);
            }
        }

        public static void PushSoon()
        {
            if (!WebsiteAuth.IsSignedIn())
                return;
            if (DateTime.UtcNow - _lastPushUtc < PushInterval)
                return;
            if (Interlocked.CompareExchange(ref _pushScheduled, 1, 0) != 0)
                return;
            _ = Task.Run(async delegate
            {
                try
                {
					await Task.Delay(TimeSpan.FromSeconds(30), LifetimeCancellation.Token).ConfigureAwait(false);
					await PushAsync(LifetimeCancellation.Token).ConfigureAwait(false);
                }
				catch (OperationCanceledException) when (LifetimeCancellation.IsCancellationRequested)
				{
				}
                catch (Exception ex)
                {
                    App.Logger.WriteException("WebsiteGeoSync::PushSoon", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _pushScheduled, 0);
                }
            });
        }

		public static Task PushAsync()
		{
			return PushAsync(CancellationToken.None);
		}

		private static async Task PushAsync(CancellationToken cancellationToken)
        {
            string? token = WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(token))
                return;
            List<object> entries = ServerFetchStore.AllEntries()
                .Where(e => e.SeenCount > 0 && !string.IsNullOrEmpty(e.Cidr) && (e.Lat != 0.0 || e.Lon != 0.0) && !VoidstrapMatchmaker.IsPrivateIp(e.Cidr.Split('/')[0]))
                .OrderByDescending(e => e.LastSeenUtc)
                .Take(MaxEntriesPerPush)
                .Select(e => (object)new
                {
                    cidr = e.Cidr,
                    city = e.City ?? "",
                    region = e.Region ?? "",
                    country = RobloxDatacenterMap.ResolveCountry(e.City, e.Country),
                    lat = e.Lat,
                    lon = e.Lon
                })
                .ToList();
            if (entries.Count == 0)
                return;
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, App.WebsiteBaseUrl + "/api/geoshare");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(new { entries }), Encoding.UTF8, "application/json");
			using HttpResponseMessage resp = await App.HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                _lastPushUtc = DateTime.UtcNow;
                App.Logger.WriteLine("WebsiteGeoSync", $"Shared {entries.Count} learned datacenter entries with the community map");
            }
            else
            {
                App.Logger.WriteLine("WebsiteGeoSync", $"Push rejected: {(int)resp.StatusCode}");
            }
        }

		public static void Shutdown()
		{
			LifetimeCancellation.Cancel();
			LifetimeCancellation.Dispose();
		}
    }
}
