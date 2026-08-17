using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Voidstrap.AppData
{
    public static class GlobalCache
    {
        private const int MaxServerLocations = 512;

        private static readonly ConcurrentDictionary<string, string?> ServerLocations = new();
        private static readonly ConcurrentQueue<string> ServerLocationOrder = new();

        public static bool TryGetServerLocation(string address, out string? location)
        {
            return ServerLocations.TryGetValue(address, out location);
        }

        public static void SetServerLocation(string address, string location)
        {
            if (ServerLocations.TryAdd(address, location))
            {
                ServerLocationOrder.Enqueue(address);
            }
            else
            {
                ServerLocations[address] = location;
            }
            while (ServerLocations.Count > MaxServerLocations && ServerLocationOrder.TryDequeue(out string? oldest))
            {
                ServerLocations.TryRemove(oldest, out _);
            }
        }
    }
}
