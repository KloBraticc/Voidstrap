using System;
using System.Collections.Generic;
using System.Linq;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed class CapabilitySet : IPlatformCapabilities
{
	private readonly IReadOnlyDictionary<FeatureId, CapabilityDescriptor> _features;

	public CapabilitySet(PlatformId platform, IEnumerable<CapabilityDescriptor> features)
	{
		Platform = platform;
		_features = features.ToDictionary(x => x.Feature);
	}

	public PlatformId Platform { get; }

	public IReadOnlyCollection<CapabilityDescriptor> Features => _features.Values.ToArray();

	public CapabilityDescriptor Get(FeatureId feature)
	{
		if (_features.TryGetValue(feature, out CapabilityDescriptor? descriptor))
		{
			return descriptor;
		}

		return new CapabilityDescriptor(feature, CapabilityState.Unavailable, "This feature is not available on this platform");
	}
}
