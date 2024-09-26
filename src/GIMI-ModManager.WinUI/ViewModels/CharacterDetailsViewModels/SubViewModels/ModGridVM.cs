﻿using System.Collections.ObjectModel;
using Windows.System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Entities;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services.ModPresetService;
using GIMI_ModManager.Core.Services.ModPresetService.Models;
using GIMI_ModManager.WinUI.Helpers;
using GIMI_ModManager.WinUI.Services.ModHandling;
using GIMI_ModManager.WinUI.Services.Notifications;

namespace GIMI_ModManager.WinUI.ViewModels.CharacterDetailsViewModels.SubViewModels;

public partial class ModGridVM(
    ISkinManagerService skinManagerService,
    CharacterSkinService characterSkinService,
    NotificationManager notificationService,
    ModPresetService presetService)
    : ObservableRecipient, IRecipient<ModChangedMessage>
{
    private readonly ISkinManagerService _skinManagerService = skinManagerService;
    private readonly CharacterSkinService _characterSkinService = characterSkinService;
    private readonly NotificationManager _notificationService = notificationService;
    private readonly ModPresetService _presetService = presetService;

    private CancellationToken _navigationCt = default;
    private ICharacterModList _modList = null!;
    private ModDetailsPageContext _context = null!;

    public bool IsDescendingSort { get; private set; } = true;
    public ModGridSortingMethod CurrentSortingMethod { get; private set; } = new(ModRowSorter.IsEnabledSorter);


    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy = true;

    public bool IsNotBusy => !IsBusy;

    private List<CharacterSkinEntry> _modsBackend = [];
    private Dictionary<Guid, ModPreset[]> _modToPresetMapping = [];
    private readonly List<ModRowVM> _gridModsBackend = [];
    public ObservableCollection<ModRowVM> GridMods { get; } = [];
    public ObservableCollection<ModRowVM> SelectedMods { get; } = [];

    public bool IsSingleModSelected => SelectedMods.Count == 1;

    public bool ModdableObjectHasAnyMods => _modsBackend.Count != 0;

    public async Task InitializeAsync(ModDetailsPageContext context, CancellationToken navigationCt = default)
    {
        _navigationCt = navigationCt;
        _context = context;
        _modList = _skinManagerService.GetCharacterModList(_context.ShownModObject);
        await ReloadAllModsAsync();
        IsBusy = false;
    }


    public async Task ReloadAllModsAsync()
    {
        GridMods.Clear();
        _gridModsBackend.Clear();
        RefreshResult refreshResult = new RefreshResult();

        await Task.Run(async () =>
        {
            refreshResult = await _skinManagerService
                .RefreshModsAsync(_context.ShownModObject.InternalName)
                .ConfigureAwait(false);

            if (_context.IsCharacter && _context.Skins.Count > 1)
            {
                _modsBackend = await _characterSkinService
                    .GetCharacterSkinEntriesForSkinAsync(_context.SelectedSkin, useSettingsCache: true,
                        cancellationToken: _navigationCt)
                    .ToListAsync().ConfigureAwait(false);
            }
            else
            {
                _modsBackend = _modList.Mods.ToList();
            }

            _modToPresetMapping = await _presetService
                .FindPresetsForModsAsync(_modsBackend.Select(m => m.Id))
                .ConfigureAwait(false);

            foreach (var x in _modsBackend)
            {
                _navigationCt.ThrowIfCancellationRequested();

                var modVm = await CreateModRowVM(x, _navigationCt).ConfigureAwait(false);
                _gridModsBackend.Add(modVm);
            }

            if (refreshResult.ModsDuplicate.Any())
            {
                var message = $"Duplicate mods were detected in {_context.ModObjectDisplayName}'s mod folder.\n";

                message = refreshResult.ModsDuplicate.Aggregate(message,
                    (current, duplicateMod) =>
                        current +
                        $"Mod: '{duplicateMod.ExistingFolderName}' was renamed to '{duplicateMod.RenamedFolderName}' to avoid conflicts.\n");

                _notificationService.ShowNotification("Duplicate Mods Detected",
                    message,
                    TimeSpan.FromSeconds(10));
            }
        }, _navigationCt);


        GridMods.AddRange(_gridModsBackend);
        SetModSorting(CurrentSortingMethod.SortingMethodType, IsDescendingSort);
    }

    private async Task<ModRowVM> CreateModRowVM(CharacterSkinEntry characterSkinEntry,
        CancellationToken cancellationToken = default)
    {
        var skinModSettings = await characterSkinEntry.Mod.Settings.TryReadSettingsAsync(true, cancellationToken)
            .ConfigureAwait(false);

        var presetNames = _modToPresetMapping.TryGetValue(characterSkinEntry.Id, out var presets)
            ? presets.Select(p => p.Name)
            : [];


        return new ModRowVM(characterSkinEntry, skinModSettings, presetNames)
        {
            ToggleEnabledCommand = new AsyncRelayCommand<ModRowVM>(ToggleModAsync)
        };
    }

    private async Task UpdateModVm(CharacterSkinEntry characterSkinEntry,
        CancellationToken cancellationToken = default)
    {
        var existingMod = _gridModsBackend.FirstOrDefault(mod => mod.Id == characterSkinEntry.Id);
        if (existingMod is null)
            throw new InvalidOperationException("Mod not found in grid mods");

        var skinModSettings = await characterSkinEntry.Mod.Settings.TryReadSettingsAsync(true, cancellationToken)
            .ConfigureAwait(false);

        var presetNames = _modToPresetMapping.TryGetValue(characterSkinEntry.Id, out var presets)
            ? presets.Select(p => p.Name)
            : [];

        existingMod.UpdateModel(characterSkinEntry, skinModSettings, presetNames);
    }

    public ModRowVM[] SearchFilterMods(string searchText)
    {
        var modsToShow = _gridModsBackend
            .Where(x => x.SearchableText.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        var modsToHide = _gridModsBackend.Except(modsToShow).ToArray();

        foreach (var mod in modsToShow)
        {
            if (GridMods.Contains(mod))
                continue;

            GridMods.Add(mod);
        }

        foreach (var modToHide in modsToHide)
            GridMods.Remove(modToHide);

        return GridMods.ToArray();
    }

    public void ResetModView()
    {
        var selectedMod = SelectedMods.FirstOrDefault();
        GridMods.Clear();
        GridMods.AddRange(_gridModsBackend);

        if (selectedMod is not null)
            SetSelectedMod(selectedMod.Id);
    }

    public void Receive(ModChangedMessage message)
    {
        // TODO: IMplement
    }

    public void SetSelectedMod(Guid modId, bool ignoreFilters = false)
    {
        if (ignoreFilters)
            throw new NotImplementedException();

        var mod = GridMods.FirstOrDefault(x => x.Id == modId);
        if (mod is null)
            return;

        var index = GridMods.IndexOf(mod);
        SelectModEvent?.Invoke(this, new SelectModRowEventArgs(index));
    }


    private async Task ToggleModAsync(ModRowVM? mod)
    {
        if (mod is null)
            return;

        var characterSkinEntry = _modsBackend.FirstOrDefault(x => x.Id == mod.Id);
        if (characterSkinEntry is null)
            return;

        try
        {
            await Task.Run(() => _modList.ToggleMod(characterSkinEntry.Id), CancellationToken.None);
            await UpdateModVm(characterSkinEntry, CancellationToken.None);
        }
        catch (Exception e)
        {
            _notificationService.ShowNotification("An error occured toggling mod", e.Message, TimeSpan.FromSeconds(5));
        }

        Messenger.Send(new ModChangedMessage(characterSkinEntry, null));
    }

    public void ClearSelection() => SelectModEvent?.Invoke(this, new SelectModRowEventArgs(-1));


    // Only to be used by code behind
    // Contrary to my attempt at MVVM, this is the only way to get the selected mods from the view
    // DataGrid is quite limited in this regard, so it is in charge of the selection and the view model just listens
    public void SelectionChanged_EventHandler(ICollection<ModRowVM> selectedMods, ICollection<ModRowVM> removedMods)
    {
        var anyChanges = false;
        if (selectedMods.Any())
        {
            foreach (var mod in selectedMods)
            {
                if (!SelectedMods.Contains(mod))
                {
                    SelectedMods.Add(mod);
                    anyChanges = true;
                }
            }
        }

        if (removedMods.Any())
        {
            foreach (var mod in removedMods)
            {
                if (SelectedMods.Contains(mod))
                {
                    SelectedMods.Remove(mod);
                    anyChanges = true;
                }
            }
        }

        if (anyChanges)
        {
            OnPropertyChanged(nameof(IsSingleModSelected));
            OnModsSelected?.Invoke(this, new ModRowSelectedEventArgs(SelectedMods));
        }
    }

    // Only to be used by code behind
    public async Task OnKeyDown_EventHandlerAsync(VirtualKey key)
    {
        var selectedMods = SelectedMods.ToArray();
        if (selectedMods.Length == 0)
            return;

        if (key == VirtualKey.Delete)
        {
            _notificationService.ShowNotification("Not implemented", "", null);
            return;
        }

        if (key == VirtualKey.Space)
        {
            if (selectedMods.Any(m => !m.ToggleEnabledCommand.CanExecute(null)))
                return;

            foreach (var mod in selectedMods)
            {
                await mod.ToggleEnabledCommand.ExecuteAsync(mod);
            }
        }
    }

    // Used by parent view model
    public event EventHandler<ModRowSelectedEventArgs>? OnModsSelected;

    // Set single selected from code
    public event EventHandler<SelectModRowEventArgs>? SelectModEvent;

    public event EventHandler<SortEvent>? SortEvent;

    public class ModRowSelectedEventArgs(IEnumerable<ModRowVM> mods) : EventArgs
    {
        public ICollection<ModRowVM> Mods { get; } = mods.ToArray();
    }

    public class SelectModRowEventArgs(int index) : EventArgs
    {
        public int Index { get; } = index;
    }

    public void SetModSorting(string sortColumn, bool isDescending, bool isUiTriggered = false)
    {
        if (sortColumn.IsNullOrEmpty())
            return;

        var sorter = ModGridSortingMethod.AllSorters.FirstOrDefault(x => x.SortingMethodType == sortColumn);

        if (sorter is null)
            return;

        CurrentSortingMethod = new ModGridSortingMethod(sorter);
        IsDescendingSort = isDescending;
        SortMods();

        if (isUiTriggered == false)
            SortEvent?.Invoke(this, new SortEvent(sortColumn, isDescending));
    }

    private void SortMods()
    {
        var sortedBackendMods = CurrentSortingMethod.Sort(_gridModsBackend, IsDescendingSort).ToArray();
        _gridModsBackend.Clear();
        _gridModsBackend.AddRange(sortedBackendMods);

        var sortedVisibleMods = CurrentSortingMethod.Sort(GridMods, IsDescendingSort).ToArray();
        for (var newPosition = 0; newPosition < sortedVisibleMods.Length; newPosition++)
        {
            var mod = sortedVisibleMods[newPosition];
            var oldPosition = GridMods.IndexOf(mod);

            if (oldPosition == newPosition)
                continue;

            GridMods.Move(oldPosition, newPosition);
        }
    }
}

public class SortEvent(string sortColumn, bool isDescending) : EventArgs
{
    public string SortColumn { get; } = sortColumn;
    public bool IsDescending { get; } = isDescending;
}


