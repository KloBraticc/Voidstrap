using System;
using System.Collections.Generic;
using System.Linq;
using Voidstrap.Enums;
using Voidstrap.Models.SettingTasks;
using Voidstrap.Models.SettingTasks.Base;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Installer;

public class ModsViewModel : NotifyPropertyChangedViewModel
{
	private readonly IReadOnlyList<BaseTask> _tasks;

	public ModPresetTask OldDeathSoundTask { get; } = new ModPresetTask("OldDeathSound", "content\\sounds\\oof.ogg", "Sounds.OldDeath.ogg");

	public ModPresetTask OldAvatarBackgroundTask { get; } = new ModPresetTask("OldAvatarBackground", "ExtraContent\\places\\Mobile.rbxl", "OldAvatarBackground.rbxl");

	public ModPresetTask OldCharacterSoundsTask { get; } = new ModPresetTask("OldCharacterSounds", new Dictionary<string, string>
	{
		{ "content\\sounds\\action_footsteps_plastic.mp3", "Sounds.OldWalk.mp3" },
		{ "content\\sounds\\action_jump.mp3", "Sounds.OldJump.mp3" },
		{ "content\\sounds\\action_get_up.mp3", "Sounds.OldGetUp.mp3" },
		{ "content\\sounds\\action_falling.mp3", "Sounds.Empty.mp3" },
		{ "content\\sounds\\action_jump_land.mp3", "Sounds.Empty.mp3" },
		{ "content\\sounds\\action_swim.mp3", "Sounds.Empty.mp3" },
		{ "content\\sounds\\impact_water.mp3", "Sounds.Empty.mp3" }
	});

	public CursorPresetTask CursorTypeTask { get; } = new CursorPresetTask();

	public IReadOnlyList<CursorType> CursorStyles { get; }

	public CursorType SelectedCursorType
	{
		get => CursorTypeTask.NewState;
		set
		{
			if (CursorTypeTask.NewState == value)
				return;
			CursorTypeTask.NewState = value;
			App.Settings.Prop.CursorType = value;
			App.Settings.Prop.HasSelectedCursorType = true;
			OnPropertyChanged();
		}
	}

	public ModsViewModel()
	{
		VoidstrapDefaultCursor.EnsureSelection();
		if (CursorTypeTask.OriginalState == CursorType.Default)
			CursorTypeTask.OriginalState = App.Settings.Prop.CursorType;
		else
			App.Settings.Prop.CursorType = CursorTypeTask.OriginalState;
		CursorStyles = CursorTypeTask.Selections.Where(static type => type != CursorType.Custom).ToArray();

		_tasks =
		[
			CursorTypeTask,
			OldAvatarBackgroundTask,
			OldCharacterSoundsTask,
			OldDeathSoundTask
		];
	}

	public void Apply()
	{
		foreach (BaseTask task in _tasks)
		{
			if (task.Changed)
			{
				try
				{
					task.Execute();
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("InstallerModsViewModel::Apply", "Could not apply " + task.Name + ": " + ex.Message);
				}
			}

			if (!task.Changed)
				App.PendingSettingTasks.Remove(task.Name);
		}

		App.Settings.Prop.CursorType = CursorTypeTask.NewState;
		App.Settings.Prop.HasSelectedCursorType = true;
		if (CursorTypeTask.NewState == CursorType.VoidstrapDefault)
			VoidstrapDefaultCursor.Apply();
		App.Settings.SaveDeferred();
	}
}
