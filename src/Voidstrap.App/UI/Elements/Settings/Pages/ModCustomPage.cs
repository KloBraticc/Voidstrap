using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Voidstrap.Integrations.Plugins;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public sealed class ModCustomPage : UiPage
{
	private readonly Border _host = new() { Margin = new Thickness(0, 0, 14, 14) };

	private PluginSidebar.ModPageInfo _info;

	private bool _renderQueued;

	public string PageKey => _info.Key;

	public string PluginName => _info.Plugin;

	public string PageId => _info.Id;

	public SymbolRegular PageIcon => Enum.TryParse(_info.Icon, ignoreCase: true, out SymbolRegular icon) ? icon : SymbolRegular.Apps24;

	public ModCustomPage(PluginSidebar.ModPageInfo info)
	{
		_info = info;
		Title = info.Title;
		Scrollable = true;
		Content = _host;
		Loaded += OnPageLoaded;
		Unloaded += OnPageUnloaded;
	}

	public void UpdateDefinition(PluginSidebar.ModPageInfo info)
	{
		_info = info;
		Title = info.Title;
		if (!ReferenceEquals(Content, _host))
			Content = _host;
		if (IsLoaded)
			RenderPage();
	}

	private void OnPageLoaded(object sender, RoutedEventArgs e)
	{
		if (!ReferenceEquals(Content, _host))
			Content = _host;
		PluginHost.PaneInvalidated -= OnPaneInvalidated;
		PluginHost.PaneInvalidated += OnPaneInvalidated;
		PluginHost.ModsChanged -= OnModsChanged;
		PluginHost.ModsChanged += OnModsChanged;
		RenderPage();
	}

	private void OnPageUnloaded(object sender, RoutedEventArgs e)
	{
		_pageRenderGeneration++;
		PluginHost.PaneInvalidated -= OnPaneInvalidated;
		PluginHost.ModsChanged -= OnModsChanged;
	}

	private void OnPaneInvalidated(string plugin)
	{
		if (string.Equals(plugin, _info.Plugin, StringComparison.Ordinal))
			QueueRender();
	}

	private void OnModsChanged()
	{
		QueueRender();
	}

	private void QueueRender()
	{
		if (_renderQueued)
			return;
		_renderQueued = true;
		Dispatcher.BeginInvoke(new Action(() =>
		{
			_renderQueued = false;
			if (IsLoaded)
				RenderPage();
		}), System.Windows.Threading.DispatcherPriority.Loaded);
	}

	private int _pageRenderGeneration;

	public async void RenderPage()
	{
		int generation = ++_pageRenderGeneration;
		string dir = Path.Combine(Paths.Plugins, _info.Plugin);
		JsonNode? tree = null;
		try
		{
			tree = await Task.Run(() => PluginHost.RenderCustomPage(_info.Plugin, _info.Id));
		}
		catch (Exception ex)
		{
			PluginHost.Problem(_info.Plugin, "the page " + _info.Id + " could not be drawn: " + PluginHost.Describe(ex));
		}
		if (generation != _pageRenderGeneration || !IsLoaded)
			return;
		try
		{
			_host.Child = tree == null ? null : PluginUiRenderer.BuildTree(tree, dir);
		}
		catch (Exception ex)
		{
			PluginHost.Problem(_info.Plugin, "the page " + _info.Id + " could not be drawn: " + PluginHost.Describe(ex));
		}
	}
}
