using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Voidstrap.Integrations.Animation;
using Voidstrap.UI.Elements.Base;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class AnimationPreviewWindow : WpfUiWindow{
	private readonly QuaternionRotation3D _rotation = new QuaternionRotation3D(Quaternion.Identity);

	private readonly Dictionary<string, Model3D> _parts = new Dictionary<string, Model3D>(StringComparer.Ordinal);

	private readonly DispatcherTimer _timer;

	private RigDefinition _rig = RobloxRig.R6();

	private AnimationData? _anim;

	private double _time;

	private double _yaw = 0.2;

	private double _pitch = 0.1;

	private double _distance = 15.0;

	private bool _dragging;

	private bool _draggingTimeline;

	private bool _playing = true;

	private bool _suppressSlider;

	private Point _lastPos;

	public AnimationPreviewWindow(byte[] data, string title)
	{
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Expected O, but got Unknown
		InitializeComponent();
		if (!string.IsNullOrEmpty(title))
		{
			base.Title = "Animation Preview: " + title;
		}
		try
		{
			_anim = RobloxAnimationParser.Parse(data);
		}
		catch
		{
			_anim = null;
		}
		if (_anim != null && _anim.IsR15)
		{
			_rig = RobloxRig.R15();
		}
		BuildScene();
		if (_anim == null || _anim.Keyframes.Count == 0)
		{
			StatusText.Text = "Couldn't read this animation, showing the rig in rest pose.";
			_playing = false;
			PlayButton.Icon = SymbolRegular.Play24;
			UpdatePose(0.0);
		}
		else
		{
			StatusText.Text = $"{_anim.Keyframes.Count} keyframes, {(_anim.IsR15 ? "R15" : "R6")}";
			UpdatePose(0.0);
		}
		_timer = new DispatcherTimer((DispatcherPriority)7)
		{
			Interval = TimeSpan.FromMilliseconds(16L)
		};
		_timer.Tick += Timer_Tick;
		_timer.Start();
		base.Closed += AnimationPreviewWindow_Closed;
	}

	private void AnimationPreviewWindow_Closed(object? sender, EventArgs e)
	{
		_timer.Stop();
		_timer.Tick -= Timer_Tick;
		base.Closed -= AnimationPreviewWindow_Closed;
		RigVisual.Content = null;
		RigVisual.Transform = null;
		_parts.Clear();
		_anim = null;
	}

	private void BuildScene()
	{
		Model3DGroup model3DGroup = new Model3DGroup();
		_parts.Clear();
		foreach (RigPart part in _rig.Parts)
		{
			if (!(part.Name == _rig.RootPart))
			{
				Model3D value = RigGeometry.BuildPart(part);
				_parts[part.Name] = value;
				model3DGroup.Children.Add(value);
			}
		}
		Transform3DGroup transform3DGroup = new Transform3DGroup();
		transform3DGroup.Children.Add(new RotateTransform3D(_rotation)
		{
			CenterX = 0.0,
			CenterY = -0.5,
			CenterZ = 0.0
		});
		RigVisual.Content = model3DGroup;
		RigVisual.Transform = transform3DGroup;
		UpdateRotation();
		UpdateCamera();
	}

	private Dictionary<string, RobloxCFrame> ComputePoses(double t)
	{
		Dictionary<string, RobloxCFrame> dictionary = new Dictionary<string, RobloxCFrame>(StringComparer.Ordinal);
		if (_anim == null || _anim.Keyframes.Count == 0)
		{
			return dictionary;
		}
		List<AnimKeyframe> keyframes = _anim.Keyframes;
		if (keyframes.Count == 1)
		{
			return keyframes[0].Poses;
		}
		int num = 0;
		for (int i = 0; i < keyframes.Count && keyframes[i].Time <= t; i++)
		{
			num = i;
		}
		if (num >= keyframes.Count - 1)
		{
			return keyframes[keyframes.Count - 1].Poses;
		}
		AnimKeyframe animKeyframe = keyframes[num];
		AnimKeyframe animKeyframe2 = keyframes[num + 1];
		double num2 = animKeyframe2.Time - animKeyframe.Time;
		double value = ((num2 > 0.0) ? ((t - animKeyframe.Time) / num2) : 0.0);
		value = Math.Clamp(value, 0.0, 1.0);
		foreach (string item in animKeyframe.Poses.Keys.Union(animKeyframe2.Poses.Keys))
		{
			RobloxCFrame value2;
			RobloxCFrame a = (animKeyframe.Poses.TryGetValue(item, out value2) ? value2 : RobloxCFrame.Identity);
			RobloxCFrame value3;
			RobloxCFrame b = (animKeyframe2.Poses.TryGetValue(item, out value3) ? value3 : RobloxCFrame.Identity);
			dictionary[item] = RobloxCFrame.Lerp(a, b, value);
		}
		return dictionary;
	}

	private Dictionary<string, RobloxCFrame> ComputeWorld(Dictionary<string, RobloxCFrame> poses)
	{
		Dictionary<string, RobloxCFrame> dictionary = new Dictionary<string, RobloxCFrame>(StringComparer.Ordinal) { [_rig.RootPart] = RobloxCFrame.Identity };
		foreach (RigJoint joint in _rig.Joints)
		{
			if (dictionary.TryGetValue(joint.Part0, out var value))
			{
				RobloxCFrame value2;
				RobloxCFrame robloxCFrame = (poses.TryGetValue(joint.Part1, out value2) ? value2 : RobloxCFrame.Identity);
				dictionary[joint.Part1] = value * joint.C0 * robloxCFrame * joint.C1.Inverse();
			}
		}
		return dictionary;
	}

	private void UpdatePose(double t)
	{
		Dictionary<string, RobloxCFrame> poses = ComputePoses(t);
		Dictionary<string, RobloxCFrame> dictionary = ComputeWorld(poses);
		foreach (KeyValuePair<string, Model3D> part in _parts)
		{
			if (dictionary.TryGetValue(part.Key, out var value))
			{
				part.Value.Transform = new MatrixTransform3D(value.ToMatrix3D());
			}
		}
		double num = _anim?.Length ?? 0.0;
		TimeText.Text = $"{t:0.00} / {num:0.00}s";
		if (!_draggingTimeline && num > 0.0)
		{
			_suppressSlider = true;
			TimelineSlider.Value = Math.Clamp(t / num, 0.0, 1.0);
			_suppressSlider = false;
		}
	}

	private void Timer_Tick(object? sender, EventArgs e)
	{
		if (!_playing || _anim == null || _anim.Length <= 0.0)
		{
			return;
		}
		double num = SpeedSlider?.Value ?? 1.0;
		_time += 0.016 * num;
		if (_time >= _anim.Length)
		{
			CheckBox loopCheck = LoopCheck;
			if (loopCheck != null && loopCheck.IsChecked == true)
			{
				_time %= _anim.Length;
			}
			else
			{
				_time = _anim.Length;
				_playing = false;
				PlayButton.Icon = SymbolRegular.Play24;
			}
		}
		UpdatePose(_time);
	}

	private void PlayButton_Click(object sender, RoutedEventArgs e)
	{
		if (_anim != null && !(_anim.Length <= 0.0))
		{
			_playing = !_playing;
			if (_playing && _time >= _anim.Length)
			{
				_time = 0.0;
			}
			PlayButton.Icon = (_playing ? SymbolRegular.Pause24 : SymbolRegular.Play24);
		}
	}

	private void Timeline_DragStarted(object sender, DragStartedEventArgs e)
	{
		_draggingTimeline = true;
	}

	private void Timeline_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		_draggingTimeline = false;
	}

	private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_suppressSlider && _anim != null && !(_anim.Length <= 0.0) && _draggingTimeline)
		{
			_time = e.NewValue * _anim.Length;
			UpdatePose(_time);
		}
	}

	private void UpdateCamera()
	{
		Camera.Position = new Point3D(0.0, -0.5, _distance);
		Camera.LookDirection = new Vector3D(0.0, 0.0, -1.0);
		Camera.UpDirection = new Vector3D(0.0, 1.0, 0.0);
	}

	private void UpdateRotation()
	{
		Quaternion quaternion = new Quaternion(new Vector3D(0.0, 1.0, 0.0), _yaw * 180.0 / Math.PI);
		Quaternion quaternion2 = new Quaternion(new Vector3D(1.0, 0.0, 0.0), _pitch * 180.0 / Math.PI);
		_rotation.Quaternion = quaternion * quaternion2;
	}

	private void ViewportHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		_dragging = true;
		_lastPos = e.GetPosition(ViewportHost);
		ViewportHost.CaptureMouse();
	}

	private void ViewportHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		_dragging = false;
		ViewportHost.ReleaseMouseCapture();
	}

	private void ViewportHost_MouseMove(object sender, MouseEventArgs e)
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		if (_dragging)
		{
			Point position = e.GetPosition(ViewportHost);
			_yaw += (position.X - _lastPos.X) * 0.01;
			_pitch += (position.Y - _lastPos.Y) * 0.01;
			_pitch = Math.Clamp(_pitch, -1.4, 1.4);
			_lastPos = position;
			UpdateRotation();
		}
	}

	private void ViewportHost_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		_distance *= ((e.Delta > 0) ? 0.9 : 1.11);
		_distance = Math.Clamp(_distance, 4.0, 60.0);
		UpdateCamera();
	}
}
