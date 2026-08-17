using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using Voidstrap.Integrations;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.ContextMenu;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class MusicPlayer : WpfUiWindow
{
	private readonly ActivityWatcher? _activityWatcher;

	private readonly MusicPlayerViewModel _viewModel;

	public MusicPlayer()
		: this(null)
	{
	}

	public MusicPlayer(ActivityWatcher? activityWatcher)
	{
		InitializeComponent();
		_activityWatcher = activityWatcher;
		_viewModel = new MusicPlayerViewModel();
		base.DataContext = _viewModel;
		base.Loaded += MusicPlayer_Loaded;
		base.Closed += MusicPlayer_Closed;
		base.PreviewKeyDown += MusicPlayer_PreviewKeyDown;
	}

	private void MusicPlayer_Loaded(object sender, RoutedEventArgs e)
	{
		_viewModel.UpdateFilteredLibrary();
	}

	private void MusicPlayer_Closed(object? sender, EventArgs e)
	{
		base.Loaded -= MusicPlayer_Loaded;
		base.Closed -= MusicPlayer_Closed;
		base.PreviewKeyDown -= MusicPlayer_PreviewKeyDown;
		try
		{
			_viewModel.Dispose();
		}
		catch
		{
		}
		base.DataContext = null;
	}

	private void MusicPlayer_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Invalid comparison between Unknown and I4
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Invalid comparison between Unknown and I4
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Expected I4, but got Unknown
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Invalid comparison between Unknown and I4
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Invalid comparison between Unknown and I4
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a5: Invalid comparison between Unknown and I4
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_013d: Invalid comparison between Unknown and I4
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Invalid comparison between Unknown and I4
		//IL_0175: Unknown result type (might be due to invalid IL or missing references)
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Invalid comparison between Unknown and I4
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Invalid comparison between Unknown and I4
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Invalid comparison between Unknown and I4
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Invalid comparison between Unknown and I4
		if (e.OriginalSource is System.Windows.Controls.TextBox)
		{
			return;
		}
		Key key = e.Key;
		if ((int)key <= 55)
		{
			switch ((int)key - 18)
			{
			default:
				if ((int)key == 55 && ((int)Keyboard.Modifiers & 2) == 2)
				{
					_viewModel.ToggleLoopCommand.Execute(null);
					e.Handled = true;
				}
				break;
			case 0:
				_viewModel.PlayPauseCommand.Execute(null);
				e.Handled = true;
				break;
			case 7:
				if (((int)Keyboard.Modifiers & 2) == 2)
				{
					_viewModel.NextCommand.Execute(null);
					e.Handled = true;
				}
				break;
			case 5:
				if (((int)Keyboard.Modifiers & 2) == 2)
				{
					_viewModel.PreviousCommand.Execute(null);
					e.Handled = true;
				}
				break;
			case 6:
				if (((int)Keyboard.Modifiers & 2) == 2)
				{
					_viewModel.Volume = Math.Min(1.0, _viewModel.Volume + 0.05);
					e.Handled = true;
				}
				break;
			case 8:
				if (((int)Keyboard.Modifiers & 2) == 2)
				{
					_viewModel.Volume = Math.Max(0.0, _viewModel.Volume - 0.05);
					e.Handled = true;
				}
				break;
			case 1:
			case 2:
			case 3:
			case 4:
				break;
			}
		}
		else if ((int)key != 56)
		{
			if ((int)key == 62 && ((int)Keyboard.Modifiers & 2) == 2)
			{
				_viewModel.ToggleShuffleCommand.Execute(null);
				e.Handled = true;
			}
		}
		else if (((int)Keyboard.Modifiers & 2) == 2)
		{
			_viewModel.ToggleMuteCommand.Execute(null);
			e.Handled = true;
		}
	}

	private void TrackTitleEdit_KeyDown(object sender, KeyEventArgs e)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Invalid comparison between Unknown and I4
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Invalid comparison between Unknown and I4
		if (sender is System.Windows.Controls.TextBox textBox)
		{
			if ((int)e.Key == 6)
			{
				textBox.IsReadOnly = true;
				Keyboard.ClearFocus();
				e.Handled = true;
			}
			else if ((int)e.Key == 13)
			{
				textBox.IsReadOnly = true;
				Keyboard.ClearFocus();
				e.Handled = true;
			}
		}
	}

	private void TrackTitle_DoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox textBox)
		{
			textBox.IsReadOnly = false;
			textBox.Focus();
			textBox.SelectAll();
			e.Handled = true;
		}
	}

	private void PositionSlider_DragStarted(object sender, DragStartedEventArgs e)
	{
		_viewModel.IsSeeking = true;
	}

	private void PositionSlider_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		_viewModel.IsSeeking = false;
	}

	private void VolumeSlider_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		double num = 0.05;
		double num2 = ((e.Delta > 0) ? num : (0.0 - num));
		_viewModel.Volume = Math.Clamp(_viewModel.Volume + num2, 0.0, 1.0);
		e.Handled = true;
	}
}
