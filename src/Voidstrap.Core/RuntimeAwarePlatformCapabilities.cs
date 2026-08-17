using System;
using System.Collections.Generic;
using System.Linq;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed class RuntimeAwarePlatformCapabilities : IPlatformCapabilities
{
	private readonly IPlatformCapabilities _baseCapabilities;
	private readonly RuntimeInstallation? _playerRuntime;
	private readonly RuntimeInstallation? _studioRuntime;

	public RuntimeAwarePlatformCapabilities(
		IPlatformCapabilities baseCapabilities,
		RuntimeInstallation? playerRuntime,
		RuntimeInstallation? studioRuntime)
	{
		_baseCapabilities = baseCapabilities ?? throw new ArgumentNullException(nameof(baseCapabilities));
		_playerRuntime = playerRuntime;
		_studioRuntime = studioRuntime;
	}

	public PlatformId Platform => _baseCapabilities.Platform;

	public IReadOnlyCollection<CapabilityDescriptor> Features => _baseCapabilities.Features
		.Select(Resolve)
		.ToArray();

	public CapabilityDescriptor Get(FeatureId feature)
	{
		return Resolve(_baseCapabilities.Get(feature));
	}

	private CapabilityDescriptor Resolve(CapabilityDescriptor capability)
	{
		return capability.Feature switch
		{
			FeatureId.RobloxPlayer when _playerRuntime is not null => _playerRuntime.Capability,
			FeatureId.RobloxStudio when _studioRuntime is not null => _studioRuntime.Capability,
			_ => capability
		};
	}
}
