using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using VoidstrapClient.WebServer.Enums;
using VoidstrapClient.WebServer.Models;

namespace VoidstrapClient.WebServer.Services;

internal class FriendsService
{
	private const int MaxRelationships = 4096;
	private readonly ConcurrentDictionary<(int First, int Second), FriendStatus> _relationships = new();
	private readonly object _admissionGate = new();

	public static FriendsService Instance { get; } = new FriendsService();

	public bool AreFriend(int user, int otherUser)
	{
		return _relationships.TryGetValue(Normalize(user, otherUser), out FriendStatus status) && status == FriendStatus.Friend;
	}

	public IEnumerable<int> AreFriends(int user, IEnumerable<int> users)
	{
		return users.Where(otherUser => AreFriend(user, otherUser)).ToArray();
	}

	public bool TryCreateFriend(int inviter, int invitee)
	{
		(int First, int Second) pair = Normalize(inviter, invitee);
		lock (_admissionGate)
		{
			if (!_relationships.ContainsKey(pair) && _relationships.Count >= MaxRelationships)
			{
				return false;
			}
			_relationships[pair] = FriendStatus.Friend;
			return true;
		}
	}

	public void BreakFriend(int inviter, int invitee)
	{
		_relationships.TryRemove(Normalize(inviter, invitee), out _);
	}

	public IEnumerable<FriendRequest> GetFriends(int user, int skip = 0)
	{
		return _relationships
			.Where(pair => pair.Value == FriendStatus.Friend && (pair.Key.First == user || pair.Key.Second == user))
			.Skip(skip)
			.Select(pair => new FriendRequest
			{
				Inviter = pair.Key.First,
				Invitee = pair.Key.Second,
				Status = pair.Value
			})
			.ToArray();
	}

	public IEnumerable<int> GetFriendIDs(int user, int skip = 0)
	{
		return GetFriends(user, skip).Select(request => request.Inviter != user ? request.Inviter : request.Invitee).ToArray();
	}

	private static (int First, int Second) Normalize(int user, int otherUser)
	{
		return user <= otherUser ? (user, otherUser) : (otherUser, user);
	}
}
