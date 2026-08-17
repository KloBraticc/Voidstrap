using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Exceptions;
using Voidstrap.Models.APIs.Roblox;
using Voidstrap.Models.RobloxApi;
using Voidstrap.Utility;

namespace Voidstrap.Models.Entities;

public class UserDetails
{
	private static readonly ConcurrentDictionary<long, UserDetails> _cache = new ConcurrentDictionary<long, UserDetails>();

	private static readonly ConcurrentQueue<long> _cacheOrder = new ConcurrentQueue<long>();

	private const int MaxCacheEntries = 256;

	public GetUserResponse Data { get; private set; }

	public ThumbnailResponse Thumbnail { get; private set; }

	public static async Task<UserDetails> Fetch(long id, CancellationToken token = default(CancellationToken))
	{
		if (_cache.TryGetValue(id, out UserDetails value))
		{
			return value;
		}
		GetUserResponse userResponse;
		try
		{
			userResponse = (await Http.GetJson<GetUserResponse>($"https://users.roblox.com/v1/users/{id}", token)) ?? throw new InvalidHTTPResponseException($"Failed to fetch user details for ID {id}");
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("UserDetails::Fetch(User)", ex);
			throw new InvalidHTTPResponseException($"Roblox users API did not respond for ID {id} Try again shortly");
		}
		ThumbnailResponse thumbnail;
		try
		{
			thumbnail = (await Http.GetJson<ApiArrayResponse<ThumbnailResponse>>($"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={id}&size=180x180&format=Png&isCircular=false", token))?.Data?.FirstOrDefault() ?? new ThumbnailResponse
			{
				TargetId = id,
				State = "Unavailable",
				ImageUrl = null
			};
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2)
		{
			App.Logger.WriteException("UserDetails::Fetch(Thumbnail)", ex2);
			thumbnail = new ThumbnailResponse
			{
				TargetId = id,
				State = "Unavailable",
				ImageUrl = null
			};
		}
		UserDetails userDetails = new UserDetails
		{
			Data = userResponse,
			Thumbnail = thumbnail
		};
		Store(id, userDetails);
		return userDetails;
	}

	private static void Store(long id, UserDetails details)
	{
		if (_cache.TryAdd(id, details))
		{
			_cacheOrder.Enqueue(id);
		}
		else
		{
			_cache[id] = details;
		}
		while (_cache.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out long oldest))
		{
			_cache.TryRemove(oldest, out _);
		}
	}
}
