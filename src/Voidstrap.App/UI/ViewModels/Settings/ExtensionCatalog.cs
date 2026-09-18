using System.Collections.Generic;

namespace Voidstrap.UI.Elements.Settings.Pages;

public static class ExtensionCatalog
{
	public const string FleasionIcon = "https://avatars.githubusercontent.com/u/186699266";

	public const string RiShadeIcon = "https://raw.githubusercontent.com/KloBraticc/RiShade/main/Images/RiShde.png";

	public const string ApiDumpIcon = "https://raw.githubusercontent.com/MaximumADHD/Roblox-API-Dump-Tool/master/Resources/AppLogo.png";

	public const string RojoIcon = "https://github.com/rojo-rbx.png";

	public const string VoidstrapIcon = "pack://application:,,,/Voidstrap.png";

	public static readonly string[] Categories = { ExtensionViewModel.TypeExtensions, ExtensionViewModel.TypeClassic, ExtensionViewModel.TypeStudio };

	public static readonly string[] Targets = { "Roblox Player", "Roblox Studio", "Classic clients" };

	public static List<ExtensionEntry> Build(ExtensionViewModel vm)
	{
		return new List<ExtensionEntry>
		{
			new ExtensionEntry("fleasion", () => vm.fleasionenabler, value => vm.fleasionenabler = value)
			{
				Name = "Fleasion",
				Author = "Fleasion",
				AuthorIcon = FleasionIcon,
				Type = ExtensionViewModel.TypeExtensions,
				WorksWith = ["Roblox Player"],
				Tags = ["Textures", "Audio", "Meshes", "Asset dumping"],
				Summary = "Swap game textures, sounds, meshes and more in real time. Can also dump game assets.",
				Description = [
					"Fleasion hooks into the assets Roblox loads and lets you replace them while a game is running. Point it at a texture, a sound or a mesh and the game picks up your version without a restart.",
					"It can also save the assets a game uses to disk so you can look at them, edit them and load them back in.",
					"Turn it on here and Voidstrap downloads the latest release for you. Use Open to launch it whenever Roblox is running."
				],
				Icon = FleasionIcon,
				Source = "https://github.com/fleasion/Fleasion",
				CanOpen = true,
				SearchText = "Fleasion replace Roblox game assets textures audio meshes animations dump"
			},
			new ExtensionEntry("rishade", () => vm.rishadeenabler, value => vm.rishadeenabler = value)
			{
				Name = "RiShade",
				Author = "Voidstrap",
				AuthorIcon = RiShadeIcon,
				Type = ExtensionViewModel.TypeExtensions,
				WorksWith = ["Roblox Player"],
				Tags = ["Shaders", "Visuals", "Screenshots"],
				Summary = "Brings shaders back to Roblox for better visuals. Press F8 in game to open its window. Best used for screenshots rather than everyday play.",
				Description = [
					"RiShade adds a post processing layer on top of Roblox with bloom, tone mapping, color grading and sharpening that Roblox itself no longer offers.",
					"Everything is adjusted from its own window. Open it with F8 or from this page, tweak the sliders and see the result live.",
					"Shaders cost frames, so it is best used for screenshots and videos rather than competitive play."
				],
				Icon = RiShadeIcon,
				CanOpen = true,
				SearchText = "RiShade shaders visuals effects bloom tonemap panel F8"
			},
			new ExtensionEntry("apidump", () => vm.apidumpenabler, value => vm.apidumpenabler = value)
			{
				Name = "Roblox API Dump Tool",
				Author = "MaximumADHD",
				AuthorIcon = ApiDumpIcon,
				Type = ExtensionViewModel.TypeExtensions,
				WorksWith = ["Roblox Player", "Roblox Studio"],
				Tags = ["API", "Developer", "Diff"],
				Summary = "Browse Roblox API classes and enums, and see what changed between Roblox versions.",
				Description = [
					"The Roblox API Dump Tool reads the API dump that ships with every Roblox version and shows every class, property, function, event and enum in a searchable tree.",
					"Compare two versions to see exactly what Roblox added, removed or changed, which is handy when a script breaks after an update.",
					"Voidstrap downloads a verified copy of the tool and keeps it next to the installed Roblox version."
				],
				Icon = ApiDumpIcon,
				Source = "https://github.com/MaximumADHD/Roblox-API-Dump-Tool",
				CanOpen = true,
				SearchText = "Roblox API Dump Tool MaximumADHD api dump diff classes members enums"
			},
			new ExtensionEntry("community", () => vm.communitycontentenabler, value => vm.communitycontentenabler = value)
			{
				Name = "Community Content",
				Author = "Voidstrap community",
				AuthorIcon = VoidstrapIcon,
				Type = ExtensionViewModel.TypeClassic,
				WorksWith = ["Classic clients"],
				Tags = ["Catalog", "Decals", "Meshes", "Audio", "Maps"],
				Summary = "Extra catalog items, decals, meshes, audio and maps for the classic Roblox clients.",
				Description = [
					"Classic Roblox clients cannot reach the modern catalog, so this pack ships extra items, decals, meshes, audio and maps made and collected by the community.",
					"Enable it and Voidstrap downloads the pack into the classic client content folders. Use Update now whenever a new pack is published."
				],
				Icon = VoidstrapIcon,
				CanOpen = false,
				SearchText = "Community Content catalog items decals meshes audio maps classic Roblox"
			},
			new ExtensionEntry("rojo", () => vm.rojoenabler, value => vm.rojoenabler = value)
			{
				Name = "Rojo",
				Author = "rojo-rbx",
				AuthorIcon = RojoIcon,
				Type = ExtensionViewModel.TypeStudio,
				WorksWith = ["Roblox Studio"],
				Tags = ["Sync", "Git", "Build"],
				Summary = "Syncs code files from your computer into Roblox Studio live.",
				Description = [
					"Rojo lets you write Luau in your own editor and keeps Roblox Studio in sync as you save. Your project lives on disk, so you can use Git, code review and every other tool your editor has.",
					"Voidstrap installs the Rojo command line for you and adds project tools: create a project, start or stop syncing, build a place file and install the Studio plugin, all from this page."
				],
				Icon = RojoIcon,
				Source = "https://github.com/rojo-rbx/rojo",
				CanOpen = false,
				HasTools = true,
				SearchText = "Rojo filesystem sync studio project git version control serve build init plugin"
			},
			new ExtensionEntry("studioplugin", () => vm.studiopluginenabler, value => vm.studiopluginenabler = value)
			{
				Name = "Voidstrap Studio plugin",
				Author = "Voidstrap",
				AuthorIcon = VoidstrapIcon,
				Type = ExtensionViewModel.TypeStudio,
				WorksWith = ["Roblox Studio"],
				Tags = ["Discord", "Rich presence", "Studio"],
				Summary = "A Voidstrap panel in Studio. Shows what you are building on Discord.",
				Description = [
					"Adds a Voidstrap panel inside Roblox Studio and reports what you are working on to Discord rich presence: the place name, the script you have open and whether you are testing or building.",
					"Nothing is uploaded anywhere. The plugin only talks to the Voidstrap running on your PC."
				],
				Icon = VoidstrapIcon,
				CanOpen = false,
				SearchText = "Voidstrap Studio plugin panel Discord rich presence rpc place script"
			}
		};
	}
}
