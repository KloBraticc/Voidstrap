using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginAnchors
{
	private sealed class Attached
	{
		public required string Plugin { get; init; }

		public required string Id { get; init; }

		public required string Anchor { get; init; }

		public required string Placement { get; init; }

		public int RenderVersion;

		public ContentControl? Host { get; set; }

		public RowDefinition? Row { get; set; }
	}

	private static readonly object Gate = new();

	private static readonly List<Attached> Items = [];

	private static readonly HashSet<string> PendingRefresh = new(StringComparer.Ordinal);

	private static bool _applyQueued;

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
		Attached item;
		Attached? removed = null;
		lock (Gate)
		{
			Attached? old = Items.FirstOrDefault(i => i.Plugin == plugin && i.Id == id);
			if (old != null && old.Anchor == anchor && old.Placement == placement)
			{
				item = old;
			}
			else
			{
				if (old != null)
				{
					Items.Remove(old);
					removed = old;
				}
				if (Items.Count(i => i.Plugin == plugin) >= MaxWidgetsPerMod)
					return Result(false, "a mod can attach at most " + MaxWidgetsPerMod + " widgets");
				item = new Attached { Plugin = plugin, Id = id, Anchor = anchor, Placement = placement };
				Items.Add(item);
			}
		}
		Dispatcher? dispatcher = Application.Current?.Dispatcher;
		if (dispatcher != null && !dispatcher.HasShutdownStarted)
		{
			if (removed != null)
				dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => Remove(removed)));
			dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => Place(item)));
		}
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
			Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Remove(item)));
	}

	public static void ClearAll()
	{
		List<Attached> all;
		lock (Gate)
		{
			all = Items.ToList();
			Items.Clear();
			PendingRefresh.Clear();
		}
		if (all.Count > 0)
			Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => all.ForEach(Remove)));
	}

	public static void ApplyAll()
	{
		Dispatcher? dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null || dispatcher.HasShutdownStarted)
			return;
		lock (Gate)
		{
			if (_applyQueued)
				return;
			_applyQueued = true;
		}
		dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
		{
			List<Attached> all;
			lock (Gate)
			{
				_applyQueued = false;
				all = Items.ToList();
			}
			foreach (Attached item in all)
				Place(item);
		}));
	}

	public static void Refresh(string plugin)
	{
		Dispatcher? dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null || dispatcher.HasShutdownStarted)
			return;
		lock (Gate)
		{
			if (!Items.Any(i => i.Plugin == plugin) || !PendingRefresh.Add(plugin))
				return;
		}
		dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
		{
			List<Attached> mine;
			lock (Gate)
			{
				PendingRefresh.Remove(plugin);
				mine = Items.Where(i => i.Plugin == plugin).ToList();
			}
			foreach (Attached item in mine)
				Place(item);
		}));
	}

	private static JsonObject Result(bool placed, string reason) => new() { ["isPlaced"] = placed, ["reason"] = reason };

	private static Voidstrap.UI.Elements.Settings.MainWindow? MainWindow() => Application.Current?.Windows.OfType<Voidstrap.UI.Elements.Settings.MainWindow>().FirstOrDefault();

	private static string Place(Attached item)
	{
		lock (Gate)
			if (!Items.Contains(item))
				return "";
		Voidstrap.UI.Elements.Settings.MainWindow? window = MainWindow();
		if (window == null)
			return "Voidstrap is still starting, the widget appears when the window opens";
		FrameworkElement? target = Find(window, item.Anchor);
		if (target == null)
			return "no element named " + item.Anchor + ", call $.ui.anchors() for the list";
		(Panel? panel, UIElement? child) = AttachablePanel(target);
		if (panel == null || child == null)
			return item.Anchor + " is inside a layout that cannot hold extra items, pick a neighbour whose attachable flag is true";
		if (item.Host != null && ReferenceEquals(item.Host.Parent, panel) && panel.Children.Contains(item.Host))
		{
			Render(item);
			return "";
		}
		if (item.Host?.Parent is Panel)
			Remove(item);
		ContentControl host = item.Host ?? new ContentControl
		{
			Margin = new Thickness(4, 0, 4, 0),
			VerticalAlignment = VerticalAlignment.Center,
			Focusable = false,
			IsHitTestVisible = false,
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
		item.Host = host;
		panel.Children.Insert(Math.Clamp(at, 0, panel.Children.Count), host);
		Render(item);
		if (panel is Grid gridPanel)
			PlaceInGrid(item, gridPanel, child, ReferenceEquals(panel, target));
		else if (panel is Canvas)
		{
			host.Margin = new Thickness(0);
			Panel.SetZIndex(host, 9999);
			Canvas.SetLeft(host, 0);
			Canvas.SetTop(host, 0);
		}
		return "";
	}

	private static void PlaceInGrid(Attached item, Grid panel, UIElement child, bool isTarget)
	{
		ContentControl host = item.Host!;
		if (isTarget && host.Content is Canvas)
		{
			host.Margin = new Thickness(0);
			host.HorizontalAlignment = HorizontalAlignment.Stretch;
			host.VerticalAlignment = VerticalAlignment.Stretch;
			Panel.SetZIndex(host, 9999);
			Grid.SetRowSpan(host, Math.Max(1, panel.RowDefinitions.Count));
			Grid.SetColumnSpan(host, Math.Max(1, panel.ColumnDefinitions.Count));
		}
		else
		{
			Grid grid = panel;
			if (grid.RowDefinitions.Count == 0)
				grid.RowDefinitions.Add(new RowDefinition());
			int row = item.Placement switch
			{
				"start" => 0,
				"end" => grid.RowDefinitions.Count,
				"before" => isTarget ? 0 : Grid.GetRow(child),
				_ => isTarget ? grid.RowDefinitions.Count : Grid.GetRow(child) + Grid.GetRowSpan(child)
			};
			row = Math.Clamp(row, 0, grid.RowDefinitions.Count);
			foreach (UIElement existing in grid.Children)
			{
				if (ReferenceEquals(existing, host))
					continue;
				int existingRow = Grid.GetRow(existing);
				if (existingRow >= row)
					Grid.SetRow(existing, existingRow + 1);
				else if (existingRow + Grid.GetRowSpan(existing) > row)
					Grid.SetRowSpan(existing, Grid.GetRowSpan(existing) + 1);
			}
			item.Row = new RowDefinition { Height = GridLength.Auto };
			grid.RowDefinitions.Insert(row, item.Row);
			Grid.SetRow(host, row);
			Grid.SetColumn(host, 0);
			Grid.SetColumnSpan(host, Math.Max(1, grid.ColumnDefinitions.Count));
			host.HorizontalAlignment = HorizontalAlignment.Stretch;
		}
	}

	private static void Render(Attached item)
	{
		if (item.Host == null)
			return;
		int version = ++item.RenderVersion;
		string plugin = item.Plugin;
		string id = item.Id;
		string dir = Path.Combine(Paths.Plugins, plugin);
		_ = Task.Run(() => PluginHost.RenderWidget(plugin, id)).ContinueWith(t =>
		{
			System.Text.Json.Nodes.JsonNode? tree = null;
			if (t.IsFaulted)
				PluginHost.Problem(plugin, "the widget " + id + " could not be drawn: " + PluginHost.Describe(t.Exception!.GetBaseException()));
			else
				tree = t.Result;
			Dispatcher? dispatcher = Application.Current?.Dispatcher;
			if (dispatcher == null || dispatcher.HasShutdownStarted)
				return;
			dispatcher.BeginInvoke(new Action(() =>
			{
				if (version != item.RenderVersion || item.Host == null)
					return;
				try
				{
					item.Host.Content = tree == null ? null : PluginUiRenderer.BuildTree(tree, dir);
					if (item.Host.Content is UIElement elem && !elem.IsHitTestVisible)
						item.Host.IsHitTestVisible = false;
					else if (item.Host.Content == null)
						item.Host.IsHitTestVisible = false;
					else
						item.Host.IsHitTestVisible = true;
					if (item.Host.Parent is Canvas)
					{
						if (item.Host.Content is FrameworkElement fe)
						{
							double l = Canvas.GetLeft(fe);
							if (!double.IsNaN(l)) Canvas.SetLeft(item.Host, l);
							double t2 = Canvas.GetTop(fe);
							if (!double.IsNaN(t2)) Canvas.SetTop(item.Host, t2);
							double r = Canvas.GetRight(fe);
							if (!double.IsNaN(r)) Canvas.SetRight(item.Host, r);
							double b = Canvas.GetBottom(fe);
							if (!double.IsNaN(b)) Canvas.SetBottom(item.Host, b);
						}
					}
				}
				catch (Exception ex)
				{
					PluginHost.Problem(plugin, "the widget " + id + " could not be drawn: " + PluginHost.Describe(ex));
				}
			}));
		});
	}

	private static void Remove(Attached item)
	{
		if (item.Host?.Parent is Panel panel)
		{
			panel.Children.Remove(item.Host);
			if (panel is Grid grid && item.Row != null)
			{
				int row = grid.RowDefinitions.IndexOf(item.Row);
				if (row >= 0)
				{
					foreach (UIElement existing in grid.Children)
					{
						int existingRow = Grid.GetRow(existing);
						if (existingRow > row)
							Grid.SetRow(existing, existingRow - 1);
						else if (existingRow < row && existingRow + Grid.GetRowSpan(existing) > row)
							Grid.SetRowSpan(existing, Grid.GetRowSpan(existing) - 1);
					}
					grid.RowDefinitions.RemoveAt(row);
				}
			}
		}
		item.Row = null;
		item.Host = null;
	}

	private static FrameworkElement? Find(Voidstrap.UI.Elements.Settings.MainWindow window, string name)
	{
		if (window.GetFrame()?.Content is DependencyObject pageContent)
		{
			if (LogicalTreeHelper.FindLogicalNode(pageContent, name) is FrameworkElement pageLogical)
				return pageLogical;
			FrameworkElement? pageFound = Descendants(pageContent).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == name);
			if (pageFound != null)
				return pageFound;
		}
		if (LogicalTreeHelper.FindLogicalNode(window, name) is FrameworkElement logical)
			return logical;
		return Descendants(window).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == name);
	}

	private static (Panel? Panel, UIElement? Child) AttachablePanel(FrameworkElement target)
	{
		if (target is StackPanel or WrapPanel or DockPanel or Grid or Canvas && target is Panel self)
			return (self, self);
		DependencyObject child = target;
		for (int depth = 0; depth < 5; depth++)
		{
			DependencyObject? parent = VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child);
			if (parent is StackPanel or WrapPanel or DockPanel or Grid or Canvas && parent is Panel panel && child is UIElement element && panel.Children.Contains(element))
				return (panel, element);
			if (parent == null || parent is Window)
				break;
			child = parent;
		}
		return (null, null);
	}

	private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
	{
		Stack<DependencyObject> stack = new();
		HashSet<DependencyObject> visited = new(ReferenceEqualityComparer.Instance);
		stack.Push(root);
		int seen = 0;
		while (stack.Count > 0 && seen < 20000)
		{
			DependencyObject node = stack.Pop();
			if (!visited.Add(node))
				continue;
			seen++;
			yield return node;
			int count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
			if (count > 0)
			{
				for (int i = count - 1; i >= 0; i--)
					stack.Push(VisualTreeHelper.GetChild(node, i));
			}
			else
			{
				foreach (object child in LogicalTreeHelper.GetChildren(node))
				{
					if (child is DependencyObject dep)
						stack.Push(dep);
				}
			}
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
