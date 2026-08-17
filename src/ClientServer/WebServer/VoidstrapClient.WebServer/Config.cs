using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoidstrapClient.Common;
using VoidstrapClient.Common.Enums;
using VoidstrapClient.Common.Models;
using VoidstrapClient.WebServer.Enums;

namespace VoidstrapClient.WebServer;

internal class Config
{
	private const long MaxConfigBytes = 16L * 1024 * 1024;
	internal class _Client
	{
		public class _CharacterCompatibility
		{
			public bool FigureBodyColours { get; set; }

			public bool TShirts { get; set; }

			public bool Hats { get; set; }

			public bool ShirtsAndPants { get; set; }

			public bool Faces { get; set; }

			public bool Heads { get; set; }

			public bool ExtendedColours { get; set; }

			public bool BodyParts { get; set; }
		}

		private ClientYear? _clientYear;

		[JsonIgnore]
		public string ClientName { get; set; } = "";

		[JsonIgnore]
		public ClientYear ClientYear => _clientYear ?? (_clientYear = new ClientYear(ClientName));

		public SignatureType Signature { get; set; }

		public CharacterLoadType CharacterLoadType { get; set; }

		public bool SignAssetScripts { get; set; } = true;

		public bool ClientWillDieIfAHttpRedirectHappens { get; set; }

		public _CharacterCompatibility CharacterCompatibility { get; set; } = new _CharacterCompatibility();
	}

	internal class _User
	{
		internal class _Launch
		{
			public string? SelectedMap { get; set; }

			public bool AutoSaveEnabled { get; set; }

			public bool ExperimentalPlayerlistEnabled { get; set; }

			public bool EggHuntMode { get; set; }

			public bool AutoSaveDebug { get; set; }

			public uint AutoSaveChangesPerPlayer { get; set; } = 100u;

			public uint AutoSaveCheckInterval { get; set; } = 1800u;

			public uint AutoSaveCooldown { get; set; } = 900u;

			public string CustomMapsDirectory { get; set; } = "";

			public List<string> DisabledAssetPacks { get; set; } = new List<string>();
		}

		internal class _Player
		{
			public int Id { get; set; }

			public string Name { get; set; } = "";

			public MembershipType Membership { get; set; }
		}

		public _Launch Launch { get; set; } = new _Launch();

		public _Player Player { get; set; } = new _Player();

		public Character Character { get; set; } = new Character();

		public Dictionary<string, string> OutfitPreferences { get; set; } = new Dictionary<string, string>();
	}

	public static Config Instance { get; private set; }

	public _Client Client { get; private set; } = new _Client();

	public _User User { get; private set; } = new _User();

	public bool IsRenderMode { get; set; }

	public static void Init(string client)
	{
		Instance = new Config();
		ClientPaths.SetClientName(client);
		if (!File.Exists(ClientPaths.Config))
		{
			throw new Exception("Config for requested client " + client + " does not exist");
		}
		_Client client2 = JsonSerializer.Deserialize<_Client>(ReadTextFile(ClientPaths.Config));
		if (client2 == null)
		{
			throw new Exception("Failed to deserialize client config for " + client);
		}
		Instance.Client = client2;
		Instance.Client.ClientName = client;
		Instance.Client.CharacterCompatibility ??= new _Client._CharacterCompatibility();
		if (!File.Exists(PathHelper.Settings))
		{
			throw new Exception("No user settings found");
		}
		_User user = JsonSerializer.Deserialize<_User>(ReadTextFile(PathHelper.Settings));
		if (user == null)
		{
			throw new Exception("Failed to deserialize user settings");
		}
		Instance.User = user;
		Instance.User.Launch ??= new _User._Launch();
		Instance.User.Launch.DisabledAssetPacks ??= new List<string>();
		Instance.User.Launch.CustomMapsDirectory ??= "";
		Instance.User.Player ??= new _User._Player();
		Instance.User.Player.Name ??= "Player";
		if (Instance.User.Player.Name.Length > 64)
			throw new InvalidDataException("The player name exceeds its limit");
		Instance.User.Character ??= new Character();
		Instance.User.Character.Equipped ??= new Dictionary<AvatarSlot, ulong>();
		Instance.User.OutfitPreferences ??= new Dictionary<string, string>();
		if (Instance.User.Launch.SelectedMap?.Length > 1024)
			Instance.User.Launch.SelectedMap = null;
		if (Instance.User.Launch.CustomMapsDirectory?.Length > 1024)
			Instance.User.Launch.CustomMapsDirectory = "";
		if (Instance.User.Launch.DisabledAssetPacks.Count > 256)
			Instance.User.Launch.DisabledAssetPacks.RemoveRange(256, Instance.User.Launch.DisabledAssetPacks.Count - 256);
		if (Instance.User.OutfitPreferences.Count > 1024)
			Instance.User.OutfitPreferences = new Dictionary<string, string>();
	}

	internal static string ReadTextFile(string path, long maximumBytes = MaxConfigBytes)
	{
		byte[] data = ReadBytesFile(path, maximumBytes);
		using MemoryStream stream = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(stream, Encoding.UTF8, true);
		return reader.ReadToEnd();
	}

	internal static byte[] ReadBytesFile(string path, long maximumBytes)
	{
		if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(maximumBytes));
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > maximumBytes)
			throw new InvalidDataException("The requested file size is invalid");
		byte[] data = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
		stream.ReadExactly(data);
		if (stream.ReadByte() != -1)
			throw new InvalidDataException("The requested file size changed while reading");
		return data;
	}

	internal static async Task<string> ReadTextFileAsync(string path, long maximumBytes, CancellationToken cancellationToken)
	{
		if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(maximumBytes));
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > maximumBytes)
			throw new InvalidDataException("The requested file size is invalid");
		byte[] data = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
		await stream.ReadExactlyAsync(data, cancellationToken);
		byte[] extra = new byte[1];
		if (await stream.ReadAsync(extra, cancellationToken) != 0)
			throw new InvalidDataException("The requested file size changed while reading");
		using MemoryStream memory = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(memory, Encoding.UTF8, true);
		return reader.ReadToEnd();
	}
}
