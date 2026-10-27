using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginAnchors
{
	private sealed class Attached
	{
		public required string Plugin { get; init; }

		public required string Id { get; init; }

		public required string Anchor { get; init; }

		public required string Placement { get; init; }

		public ContentControl? Host { get; set; }
	}

	private static readonly object Gate = new();

	private static readonly List<Attached> Items = [];

	private static readonly string[] Placements = ["before", "after", "start", "end"];

	private const int MaxWidgetsPerMod = 8;

	private const int MaxMapEntries = 400;

	static PluginAnchors()
	{
		PluginHost.PaneInvalidated += Refresh;
	}

	public static JsonObject Attach(string plugin, JsonObject request)
	{
		string id = request["id"]?.ToString() ?? "";
		string anchor = request["anchor"]?.ToString() ?? "";
		string placement = (request["placement"]?.ToString() ?? "after").ToLowerInvariant();
		if (id.Length is 0 or > 64 || anchor.Length is 0 or > 128)
			return Result(false, "attach needs an id and the anchor name of an element from $.ui.anchors()");
		if (!Placements.Contains(placement))
			return Result(false, "placement must be before, after, start or end");
		Attached item = new() { Plugin = plugin, Id = id, Anchor = anchor, Placement = placement };
		lock (Gate)
		{
			Attached? old = Items.FirstOrDefault(i => i.Plugin == plugin && i.Id == id);
			if (old != null)
			{
				Items.Remove(old);
				Application.Current?.Dispatcher.BeginInvoke(new Action(() => Remove(old)));
			}
			if (Items.Count(i => i.Plugin == plugin) >= MaxWidgetsPerMod)
				return Result(false, "a mod can attach at most " + MaxWidgetsPerMod + " widgets");
			Items.Add(item);
		}
		Application.Current?.Dispatcher.BeginInvoke(new Action(() => Place(item)));
		return Result(true, "");
	}

	public static void Detach(string plugin, string id)
	{
		Attached? item;
		lock (Gate)
		{
			item = Items.FirstOrDefault(i => i.Plugin == plugin && i.Id == id);
			if (item != null)
				Items.Remove(item);
		}
		if (item != null)
			Application.Current?.Dispatcher.BeginInvoke(new Action(() => Remove(item)));
	}

	public static void ClearAll()
	{
		List<Attached> all;
		lock (Gate)
		{
			all = Items.ToList();
			Items.Clear();
		}
		if (all.Count > 0)
			Application.Current?.Dispatcher.BeginInvoke(new Action(() => all.ForEach(Remove)));
	}

	public static void ApplyAll()
	{
		Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
		{
			List<Attached> all;
			lock (Gate)
				all = Items.ToList();
			foreach (Attached item in all)
				Place(item);
		}));
	}

	public static void Refresh(string plugin)
	{
		bool any;
		lock (Gate)
			any = Items.Any(i => i.Plugin == plugin);
		if (!any)
			return;
		Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
		{
			List<Attached> mine;
			lock (Gate)
				mine = Items.Where(i => i.Plugin == plugin).ToList();
			foreach (Attached item in mine)
			{
				if (item.Host != null)
					Render(item);
			}
		}));
	}

	private static JsonObject Result(bool placed, string reason) => new() { ["isPlaced"] = placed, ["reason"] = reason };

	private static Window? MainWindow() => Application.Current?.Windows.OfType<Voidstrap.UI.Elements.Settings.MainWindow>().FirstOrDefault();

	private static string Place(Attached item)
	{
		Window? window = MainWindow();
		if (window == null)
			return "Voidstrap is still starting, the widget appears when the window opens";
		if (item.Host != null && item.Host.Parent != null)
			return "";
		FrameworkElement? target = Find(window, item.Anchor);
		if (target == null)
			return "no element named " + item.Anchor + ", call $.ui.anchors() for the list";
		(Panel? panel, UIElement? child) = AttachablePanel(target);
		if (panel == null || child == null)
			return item.Anchor + " is inside a layout that cannot hold extra items, pick a neighbour whose attachable flag is true";
		ContentControl host = new()
		{
			Margin = new Thickness(4, 0, 4, 0),
			VerticalAlignment = VerticalAlignment.Center,
			Focusable = false,
			Tag = "mod:" + item.Plugin + ":" + item.Id
		};
		int index = panel.Children.IndexOf(child);
		int at = item.Placement switch
		{
			"before" => index,
			"start" => 0,
			"end" => panel.Children.Count,
			_ => index + 1
		};
		if (ReferenceEquals(panel, target) && item.Placement is "before" or "after")
			at = item.Placement == "before" ? 0 : panel.Children.Count;
		panel.Children.Insert(Math.Clamp(at, 0, panel.Children.Count), host);
		item.Host = host;
		Render(item);
		return "";
	}

	private static void Render(Attached item)
	{
		if (item.Host == null)
			return;
		try
		{
			string dir = Path.Combine(Paths.Plugins, item.Plugin);
			item.Host.Content = PluginUiRenderer.BuildTree(PluginHost.RenderWidget(item.Plugin, item.Id), dir);
		}
		catch (Exception ex)
		{
			PluginHost.Problem(item.Plugin, "the widget " + item.Id + " could not be drawn: " + PluginHost.Describe(ex));
		}
	}

	private static void Remove(Attached item)
	{
		if (item.Host?.Parent is Panel panel)
			panel.Children.Remove(item.Host);
		item.Host = null;
	}

	private static FrameworkElement? Find(Window window, string name)
	{
		if (LogicalTreeHelper.FindLogicalNode(window, name) is FrameworkElement logical)
			return logical;
		return Descendants(window).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == name);
	}

	private static (Panel? Panel, UIElement? Child) AttachablePanel(FrameworkElement target)
	{
		if (target is StackPanel or WrapPanel or DockPanel && target is Panel self)
			return (self, self);
		DependencyObject child = target;
		for (int depth = 0; depth < 3; depth++)
		{
			DependencyObject? parent = VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child);
			if (parent is StackPanel or WrapPanel or DockPanel && parent is Panel panel && child is UIElement element && panel.Children.Contains(element))
				return (panel, element);
			if (parent == null || parent is Grid || parent is Window)
				break;
			child = parent;
		}
		return (null, null);
	}

	private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
	{
		Stack<DependencyObject> stack = new();
		stack.Push(root);
		int seen = 0;
		while (stack.Count > 0 && seen < 20000)
		{
			DependencyObject node = stack.Pop();
			seen++;
			yield return node;
			int count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
			for (int i = count - 1; i >= 0; i--)
				stack.Push(VisualTreeHelper.GetChild(node, i));
		}
	}

	public static JsonArray Map()
	{
		JsonArray result = [];
		Window? window = MainWindow();
		if (window == null)
			return result;
		foreach (FrameworkElement element in Descendants(window).OfType<FrameworkElement>())
		{
			if (result.Count >= MaxMapEntries)
				break;
			if (string.IsNullOrEmpty(element.Name) || element.Name.StartsWith("PART_", StringComparison.Ordinal) || !element.IsVisible || element.ActualWidth <= 0)
				continue;
			Rect bounds;
			try
			{
				bounds = element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
			}
			catch (InvalidOperationException)
			{
				continue;
			}
			string text = element switch
			{
				TextBlock t => t.Text,
				HeaderedItemsControl h => h.Header?.ToString() ?? "",
				ContentControl c when c.Content is string s => s,
				_ => element.ToolTip as string ?? System.Windows.Automation.AutomationProperties.GetName(element)
			};
			FrameworkElement? parent = VisualTreeHelper.GetParent(element) as FrameworkElement;
			result.Add(new JsonObject
			{
				["name"] = element.Name,
				["type"] = element.GetType().Name,
				["text"] = text.Length > 80 ? text[..80] : text,
				["parent"] = parent?.Name ?? parent?.GetType().Name ?? "",
				["x"] = Math.Round(bounds.X),
				["y"] = Math.Round(bounds.Y),
				["width"] = Math.Round(bounds.Width),
				["height"] = Math.Round(bounds.Height),
				["attachable"] = AttachablePanel(element).Panel != null
			});
		}
		return result;
	}
}
