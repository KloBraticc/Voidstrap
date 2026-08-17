using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed record ExtensionManifest(
	string Id,
	string DisplayName,
	IReadOnlyCollection<PlatformId> SupportedPlatforms,
	IReadOnlyCollection<FeatureId> RequiredFeatures,
	IReadOnlyDictionary<PlatformId, IReadOnlyCollection<string>> NativeAssets,
	IReadOnlyCollection<string> ExternalRequirements);

public sealed record ExtensionAvailability(
	ExtensionManifest Manifest,
	CapabilityDescriptor Capability,
	IReadOnlyCollection<CapabilityDescriptor> Requirements);

public static class ExtensionManifestStore
{
	private const int MaximumManifestBytes = 1024 * 1024;

	private const int MaximumManifests = 256;

	private const int MaximumNativeAssetsPerPlatform = 1024;

	private const int MaximumExternalRequirements = 256;

	private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() }
	};

	public static async Task<OperationResult<ExtensionManifest>> LoadAsync(string filePath, CancellationToken cancellationToken = default)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
			{
				return OperationResult<ExtensionManifest>.Fail("ExtensionManifestMissing", "The extension manifest does not exist");
			}

			await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (stream.Length <= 0 || stream.Length > MaximumManifestBytes)
				return OperationResult<ExtensionManifest>.Fail("ExtensionManifestInvalid", "The extension manifest size is invalid");
			byte[] data = new byte[checked((int)stream.Length)];
			int offset = 0;
			while (offset < data.Length)
			{
				int read = await stream.ReadAsync(data.AsMemory(offset), cancellationToken);
				if (read == 0)
					return OperationResult<ExtensionManifest>.Fail("ExtensionManifestInvalid", "The extension manifest ended unexpectedly");
				offset += read;
			}
			if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
				return OperationResult<ExtensionManifest>.Fail("ExtensionManifestInvalid", "The extension manifest changed while it was being read");
			ExtensionManifest? manifest = JsonSerializer.Deserialize<ExtensionManifest>(data, SerializerOptions);
			if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 64 || string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 128)
			{
				return OperationResult<ExtensionManifest>.Fail("ExtensionManifestInvalid", "The extension manifest is invalid");
			}

			return OperationResult<ExtensionManifest>.Success(Normalize(manifest));
		}
		catch (OperationCanceledException)
		{
			return OperationResult<ExtensionManifest>.Fail("OperationCanceled", "Extension manifest loading was canceled");
		}
		catch (JsonException exception)
		{
			return OperationResult<ExtensionManifest>.Fail("ExtensionManifestParseFailed", exception.Message);
		}
		catch (IOException exception)
		{
			return OperationResult<ExtensionManifest>.Fail("ExtensionManifestReadFailed", exception.Message);
		}
		catch (UnauthorizedAccessException exception)
		{
			return OperationResult<ExtensionManifest>.Fail("ExtensionManifestAccessDenied", exception.Message, CapabilityState.RequiresPermission);
		}
	}

	public static async Task<OperationResult<IReadOnlyCollection<ExtensionManifest>>> LoadDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
	{
		if (!Directory.Exists(directoryPath))
		{
			return OperationResult<IReadOnlyCollection<ExtensionManifest>>.Success(Array.Empty<ExtensionManifest>());
		}

		List<ExtensionManifest> manifests = new();
		try
		{
			foreach (string path in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly).Take(MaximumManifests))
			{
				cancellationToken.ThrowIfCancellationRequested();
				OperationResult<ExtensionManifest> result = await LoadAsync(path, cancellationToken);
				if (!result.Succeeded || result.Value is null)
				{
					return CopyFailure<IReadOnlyCollection<ExtensionManifest>>(result.Failure);
				}

				manifests.Add(result.Value);
			}
		}
		catch (OperationCanceledException)
		{
			return OperationResult<IReadOnlyCollection<ExtensionManifest>>.Fail("OperationCanceled", "Extension manifest discovery was canceled");
		}
		catch (IOException exception)
		{
			return OperationResult<IReadOnlyCollection<ExtensionManifest>>.Fail("ExtensionManifestReadFailed", exception.Message);
		}
		catch (UnauthorizedAccessException exception)
		{
			return OperationResult<IReadOnlyCollection<ExtensionManifest>>.Fail("ExtensionManifestAccessDenied", exception.Message, CapabilityState.RequiresPermission);
		}

		return OperationResult<IReadOnlyCollection<ExtensionManifest>>.Success(manifests);
	}

	private static ExtensionManifest Normalize(ExtensionManifest manifest)
	{
		Dictionary<PlatformId, IReadOnlyCollection<string>> nativeAssets = new();
		foreach ((PlatformId platform, IReadOnlyCollection<string>? assets) in (manifest.NativeAssets ?? new Dictionary<PlatformId, IReadOnlyCollection<string>>()).Take(64))
		{
			nativeAssets[platform] = assets?
				.Where(static asset => !string.IsNullOrWhiteSpace(asset) && asset.Length <= 1024)
				.Select(static asset => asset.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Take(MaximumNativeAssetsPerPlatform)
				.ToArray() ?? Array.Empty<string>();
		}
		return manifest with
		{
			Id = manifest.Id.Trim(),
			DisplayName = manifest.DisplayName.Trim(),
			SupportedPlatforms = manifest.SupportedPlatforms?.Distinct().Take(64).ToArray() ?? Array.Empty<PlatformId>(),
			RequiredFeatures = manifest.RequiredFeatures?.Distinct().Take(256).ToArray() ?? Array.Empty<FeatureId>(),
			NativeAssets = nativeAssets,
			ExternalRequirements = manifest.ExternalRequirements?.Where(static requirement => !string.IsNullOrWhiteSpace(requirement) && requirement.Length <= 1024).Select(static requirement => requirement.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumExternalRequirements).ToArray() ?? Array.Empty<string>()
		};
	}

	private static OperationResult<T> CopyFailure<T>(OperationFailure? failure)
	{
		return failure is null
			? OperationResult<T>.Fail("ExtensionManifestReadFailed", "Extension manifest loading failed")
			: OperationResult<T>.Fail(failure.Code, failure.Message, failure.State);
	}
}

public static class ExtensionCapabilityEvaluator
{
	public static ExtensionAvailability Evaluate(ExtensionManifest manifest, IPlatformCapabilities capabilities, PlatformStoragePaths? storage = null)
	{
		if (!manifest.SupportedPlatforms.Contains(capabilities.Platform))
		{
			return new ExtensionAvailability(
				manifest,
				new CapabilityDescriptor(FeatureId.ExtensionNativeAssets, CapabilityState.Unavailable, $"{manifest.DisplayName} is not supported on {capabilities.Platform}"),
				Array.Empty<CapabilityDescriptor>());
		}

		CapabilityDescriptor[] requirements = manifest.RequiredFeatures
			.Select(capabilities.Get)
			.ToArray();
		CapabilityDescriptor? unavailable = requirements.FirstOrDefault(static descriptor => descriptor.State == CapabilityState.Unavailable);
		if (unavailable is not null)
		{
			return new ExtensionAvailability(
				manifest,
				new CapabilityDescriptor(FeatureId.ExtensionNativeAssets, CapabilityState.Unavailable, unavailable.Reason, unavailable.RequiredAction),
				requirements);
		}

		CapabilityDescriptor? permission = requirements.FirstOrDefault(static descriptor => descriptor.State == CapabilityState.RequiresPermission);
		if (permission is not null)
		{
			return new ExtensionAvailability(
				manifest,
				new CapabilityDescriptor(FeatureId.ExtensionNativeAssets, CapabilityState.RequiresPermission, permission.Reason, permission.RequiredAction),
				requirements);
		}

		CapabilityDescriptor? runtime = requirements.FirstOrDefault(static descriptor => descriptor.State == CapabilityState.RequiresExternalRuntime);
		if (runtime is not null)
		{
			return new ExtensionAvailability(
				manifest,
				new CapabilityDescriptor(FeatureId.ExtensionNativeAssets, CapabilityState.RequiresExternalRuntime, runtime.Reason, runtime.RequiredAction),
				requirements);
		}

		if (manifest.NativeAssets.TryGetValue(capabilities.Platform, out IReadOnlyCollection<string>? assets) && assets.Count > 0)
		{
			string? missingAsset = FindMissingAsset(assets, storage);
			if (missingAsset is not null)
			{
				return new ExtensionAvailability(
					manifest,
					new CapabilityDescriptor(
						FeatureId.ExtensionNativeAssets,
						CapabilityState.RequiresExternalRuntime,
						$"{manifest.DisplayName} requires {missingAsset}",
						"Install the required extension asset"),
					requirements);
			}
		}

		if (manifest.ExternalRequirements.Count > 0)
		{
			return new ExtensionAvailability(
				manifest,
				new CapabilityDescriptor(
					FeatureId.ExtensionNativeAssets,
					CapabilityState.RequiresExternalRuntime,
					$"{manifest.DisplayName} requires {manifest.ExternalRequirements.First()}",
					"Install the required external runtime"),
				requirements);
		}

		bool experimental = requirements.Any(static descriptor => descriptor.State == CapabilityState.Experimental || descriptor.IsExperimental);
		return new ExtensionAvailability(
			manifest,
			new CapabilityDescriptor(
				FeatureId.ExtensionNativeAssets,
				experimental ? CapabilityState.Experimental : CapabilityState.Available,
				experimental ? $"{manifest.DisplayName} is available with experimental platform support" : $"{manifest.DisplayName} is available",
				null,
				experimental),
			requirements);
	}

	private static string? FindMissingAsset(IReadOnlyCollection<string> assets, PlatformStoragePaths? storage)
	{
		if (storage is null)
		{
			return assets.First();
		}

		string root = Path.GetFullPath(storage.Extensions);
		string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
			? root
			: root + Path.DirectorySeparatorChar;
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		foreach (string asset in assets)
		{
			try
			{
				string path = Path.GetFullPath(Path.Combine(root, asset));
				if (!path.StartsWith(rootWithSeparator, comparison) || !File.Exists(path))
					return asset;
			}
			catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
			{
				return asset;
			}
		}

		return null;
	}
}
