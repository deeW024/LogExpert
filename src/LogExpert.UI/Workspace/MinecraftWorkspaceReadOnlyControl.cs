using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

using LogExpert.Core.Classes.MinecraftLogs;

namespace LogExpert.UI.Workspace;

[SupportedOSPlatform("windows")]
public sealed class MinecraftWorkspaceReadOnlyControl : UserControl
{
    private readonly int _uiThreadId;
    private readonly Label _workspaceNameLabel;
    private readonly Label _statusLabel;
    private readonly TextBox _searchTextBox;
    private readonly ComboBox _textScopeComboBox;
    private readonly CheckBox _regexCheckBox;
    private readonly CheckBox _caseSensitiveCheckBox;
    private readonly CheckBox _invertCheckBox;
    private readonly Button _clearFiltersButton;
    private readonly CheckBox _followCheckBox;
    private readonly Button _pauseButton;
    private readonly Label _liveStateLabel;
    private readonly TabControl _facetTabs;
    private readonly CheckedListBox _fileFacetList;
    private readonly CheckedListBox _sourceFacetList;
    private readonly CheckedListBox _componentFacetList;
    private readonly CheckedListBox _levelFacetList;
    private readonly CheckedListBox _threadFacetList;
    private readonly DataGridView _eventGrid;
    private readonly TextBox _detailsTextBox;
    private readonly HashSet<string> _selectedFileIds = new(StringComparer.Ordinal);
    private HashSet<MinecraftWorkspaceFacetValue<string>> _selectedSources = [];
    private HashSet<MinecraftWorkspaceFacetValue<string>> _selectedComponents = [];
    private HashSet<MinecraftWorkspaceFacetValue<LogLevel>> _selectedLevels = [];
    private HashSet<MinecraftWorkspaceFacetValue<string>> _selectedThreads = [];
    private IReadOnlyDictionary<long, int> _rowIndexByIdentity = new Dictionary<long, int>();
    private MinecraftWorkspaceReadOnlyViewSnapshot _snapshot;
    private bool _synchronizingEditor;
    private bool _suppressFollowScrollTracking;

    public MinecraftWorkspaceReadOnlyControl (MinecraftWorkspaceReadOnlyViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;
        _uiThreadId = Environment.CurrentManagedThreadId;
        Name = "MinecraftWorkspaceReadOnlyControl";
        Dock = DockStyle.Fill;
        Size = new Size(960, 640);
        MinimumSize = new Size(600, 360);

        _workspaceNameLabel = new Label
        {
            Name = "WorkspaceNameLabel",
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            Height = 27,
            Padding = new Padding(8, 5, 8, 0),
            Font = SystemFonts.MessageBoxFont
        };
        _statusLabel = new Label
        {
            Name = "WorkspaceStatusLabel",
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 8, 4),
            Font = SystemFonts.MessageBoxFont
        };

        _searchTextBox = new TextBox { Name = "WorkspaceSearchText", Width = 220 };
        _textScopeComboBox = new ComboBox
        {
            Name = "WorkspaceTextScope",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 165
        };
        _textScopeComboBox.Items.AddRange(["Message", "RawText", "Message or RawText"]);
        _regexCheckBox = new CheckBox { Name = "WorkspaceRegex", Text = Resources.LogWindow_UI_CheckBox_FilterRegex, AutoSize = true };
        _caseSensitiveCheckBox = new CheckBox { Name = "WorkspaceCaseSensitive", Text = Resources.LogWindow_UI_CheckBox_FilterCaseSensitive, AutoSize = true };
        _invertCheckBox = new CheckBox
        {
            Name = "WorkspaceInvert",
            Text = Resources.ResourceManager.GetString("MinecraftWorkspace_Filter_Invert", CultureInfo.CurrentUICulture)!,
            AutoSize = true
        };
        _clearFiltersButton = new Button
        {
            Name = "WorkspaceClearFilters",
            Text = Resources.ResourceManager.GetString("MinecraftWorkspace_Filter_ClearAll", CultureInfo.CurrentUICulture)!,
            AutoSize = true
        };
        _followCheckBox = new CheckBox
        {
            Name = "WorkspaceFollow",
            Text = Resources.ResourceManager.GetString("MinecraftWorkspace_Live_Follow", CultureInfo.CurrentUICulture)!,
            AutoSize = true,
            Checked = true
        };
        _pauseButton = new Button
        {
            Name = "WorkspacePause",
            AutoSize = true
        };
        _liveStateLabel = new Label
        {
            Name = "WorkspaceLiveState",
            AutoSize = true,
            Margin = new Padding(4, 6, 1, 0)
        };
        UpdateLiveStateText();

        FlowLayoutPanel filterEditor = new()
        {
            Name = "WorkspaceFilterEditor",
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            Padding = new Padding(6, 5, 6, 2)
        };
        filterEditor.Controls.Add(CreateFieldLabel(Resources.LogWindow_UI_Button_Search));
        filterEditor.Controls.Add(_searchTextBox);
        filterEditor.Controls.Add(CreateFieldLabel(Resources.ResourceManager.GetString("MinecraftWorkspace_Filter_SearchIn", CultureInfo.CurrentUICulture)!));
        filterEditor.Controls.Add(_textScopeComboBox);
        filterEditor.Controls.Add(_regexCheckBox);
        filterEditor.Controls.Add(_caseSensitiveCheckBox);
        filterEditor.Controls.Add(_invertCheckBox);
        filterEditor.Controls.Add(_clearFiltersButton);
        filterEditor.Controls.Add(_followCheckBox);
        filterEditor.Controls.Add(_pauseButton);
        filterEditor.Controls.Add(_liveStateLabel);

        _facetTabs = new TabControl { Name = "WorkspaceFacets", Dock = DockStyle.Fill };
        _fileFacetList = CreateFacetList("WorkspaceFileFacet");
        _sourceFacetList = CreateFacetList("WorkspaceSourceFacet");
        _componentFacetList = CreateFacetList("WorkspaceComponentFacet");
        _levelFacetList = CreateFacetList("WorkspaceLevelFacet");
        _threadFacetList = CreateFacetList("WorkspaceThreadFacet");
        AddFacetTab("File", _fileFacetList);
        AddFacetTab("Source", _sourceFacetList);
        AddFacetTab("Component", _componentFacetList);
        AddFacetTab("Level", _levelFacetList);
        AddFacetTab("Thread", _threadFacetList);

        _eventGrid = CreateGrid();
        _detailsTextBox = new TextBox
        {
            Name = "WorkspaceEventDetails",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false
        };

        Panel header = new() { Name = "WorkspaceHeader", Dock = DockStyle.Fill };
        header.Controls.Add(_statusLabel);
        header.Controls.Add(_workspaceNameLabel);

        SplitContainer contentSplit = new()
        {
            Name = "WorkspaceContentSplit",
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Size = new Size(960, 584),
            SplitterDistance = 250,
            Panel1MinSize = 180,
            Panel2MinSize = 320
        };
        contentSplit.Panel1.Controls.Add(_facetTabs);

        SplitContainer eventSplit = new()
        {
            Name = "WorkspaceEventSplit",
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            Size = new Size(700, 584),
            SplitterDistance = 350,
            Panel1MinSize = 120,
            Panel2MinSize = 120
        };
        eventSplit.Panel1.Controls.Add(_eventGrid);
        eventSplit.Panel2.Controls.Add(_detailsTextBox);
        contentSplit.Panel2.Controls.Add(eventSplit);

        TableLayoutPanel root = new()
        {
            Name = "WorkspaceRoot",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(filterEditor, 0, 0);
        root.Controls.Add(header, 0, 1);
        root.Controls.Add(contentSplit, 0, 2);
        Controls.Add(root);

        _searchTextBox.TextChanged += OnTextQueryChanged;
        _textScopeComboBox.SelectedIndexChanged += OnTextQueryChanged;
        _regexCheckBox.CheckedChanged += OnTextQueryChanged;
        _caseSensitiveCheckBox.CheckedChanged += OnTextQueryChanged;
        _invertCheckBox.CheckedChanged += OnTextQueryChanged;
        _clearFiltersButton.Click += OnClearFilters;
        _followCheckBox.CheckedChanged += OnFollowChanged;
        _pauseButton.Click += OnPauseClicked;
        _fileFacetList.ItemCheck += (_, e) => OnFacetItemCheck(_fileFacetList, _selectedFileIds, e);
        _sourceFacetList.ItemCheck += (_, e) => OnFacetItemCheck(_sourceFacetList, _selectedSources, e);
        _componentFacetList.ItemCheck += (_, e) => OnFacetItemCheck(_componentFacetList, _selectedComponents, e);
        _levelFacetList.ItemCheck += (_, e) => OnFacetItemCheck(_levelFacetList, _selectedLevels, e);
        _threadFacetList.ItemCheck += (_, e) => OnFacetItemCheck(_threadFacetList, _selectedThreads, e);

        ApplySnapshot(snapshot);
    }

    public event EventHandler<MinecraftWorkspaceFilterQueryChangedEventArgs>? FilterQueryChanged;

    public event EventHandler? PauseChanged;

    public MinecraftWorkspaceReadOnlyViewSnapshot Snapshot => _snapshot;

    public TextBox SearchTextBox => _searchTextBox;

    public ComboBox TextScopeComboBox => _textScopeComboBox;

    public CheckBox RegexCheckBox => _regexCheckBox;

    public CheckBox CaseSensitiveCheckBox => _caseSensitiveCheckBox;

    public CheckBox InvertCheckBox => _invertCheckBox;

    public Button ClearFiltersButton => _clearFiltersButton;

    public CheckBox FollowCheckBox => _followCheckBox;

    public Button PauseButton => _pauseButton;

    public Label LiveStateLabel => _liveStateLabel;

    public bool IsFollowEnabled => _followCheckBox.Checked;

    public bool IsPaused { get; private set; }

    public int BacklogCount { get; private set; }

    public TabControl FacetTabs => _facetTabs;

    public CheckedListBox FileFacetList => _fileFacetList;

    public CheckedListBox SourceFacetList => _sourceFacetList;

    public CheckedListBox ComponentFacetList => _componentFacetList;

    public CheckedListBox LevelFacetList => _levelFacetList;

    public CheckedListBox ThreadFacetList => _threadFacetList;

    public DataGridView EventGrid => _eventGrid;

    public TextBox DetailsTextBox => _detailsTextBox;

    public Label StatusLabel => _statusLabel;

    public long? SelectedIdentity { get; private set; }

    public MinecraftWorkspaceTimelineEntry? SelectedEntry { get; private set; }

    public MinecraftWorkspaceReadOnlyEventDetails? SelectedDetails { get; private set; }

    /// <summary>Rebinds a completed presenter snapshot while preserving query and selection identity.</summary>
    public void ApplySnapshot (MinecraftWorkspaceReadOnlyViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        VerifyUiThread();

        long? previouslySelectedIdentity = SelectedIdentity;
        int previousFirstDisplayedRow = _eventGrid.RowCount == 0 ? 0 : Math.Max(0, _eventGrid.FirstDisplayedScrollingRowIndex);
        long? anchorIdentity = !IsFollowEnabled && previousFirstDisplayedRow < _snapshot.Rows.Count
            ? _snapshot.Rows[previousFirstDisplayedRow].Identity
            : null;
        int horizontalOffset = _eventGrid.HorizontalScrollingOffset;
        _suppressFollowScrollTracking = true;
        _synchronizingEditor = true;
        try
        {
            _eventGrid.ClearSelection();
            _eventGrid.CurrentCell = null;
            _eventGrid.RowCount = 0;

            _snapshot = snapshot;
            _rowIndexByIdentity = snapshot.Rows
                .Select((row, index) => (row.Identity, index))
                .ToDictionary(pair => pair.Identity, pair => pair.index);

            SynchronizeEditor(snapshot.QuerySnapshot);
            RenderHeader();
            _eventGrid.RowCount = snapshot.Rows.Count;
            _eventGrid.ClearSelection();
            _eventGrid.CurrentCell = null;
            _eventGrid.Invalidate();

            ClearSelectionAndDetails();
            if (previouslySelectedIdentity is long identity && _rowIndexByIdentity.TryGetValue(identity, out int newRowIndex))
            {
                _eventGrid.CurrentCell = _eventGrid.Rows[newRowIndex].Cells[0];
                _eventGrid.Rows[newRowIndex].Selected = true;
                SelectIdentity(identity);
            }

            if (IsFollowEnabled)
            {
                ScrollToTail();
            }
            else
            {
                int anchorRow = anchorIdentity is long priorIdentity && _rowIndexByIdentity.TryGetValue(priorIdentity, out int anchoredIndex)
                    ? anchoredIndex
                    : Math.Clamp(previousFirstDisplayedRow, 0, Math.Max(0, snapshot.Rows.Count - 1));
                if (snapshot.Rows.Count > 0)
                {
                    _eventGrid.FirstDisplayedScrollingRowIndex = anchorRow;
                }

                if (horizontalOffset > 0)
                {
                    try
                    {
                        _eventGrid.HorizontalScrollingOffset = horizontalOffset;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // A resized grid may expose less horizontal range than the captured view.
                    }
                }
            }
        }
        finally
        {
            _synchronizingEditor = false;
            _suppressFollowScrollTracking = false;
        }
    }

    private static Label CreateFieldLabel (string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(4, 6, 1, 0)
    };

    private static CheckedListBox CreateFacetList (string name) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false,
        HorizontalScrollbar = true
    };

    private void AddFacetTab (string title, CheckedListBox list)
    {
        TabPage page = new(title) { Name = $"{title}FacetTab", Padding = new Padding(0) };
        page.Controls.Add(list);
        _facetTabs.TabPages.Add(page);
    }

    private DataGridView CreateGrid ()
    {
        DataGridView grid = new()
        {
            Name = "WorkspaceTimelineGrid",
            Dock = DockStyle.Fill,
            VirtualMode = true,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToOrderColumns = false,
            AutoGenerateColumns = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false
        };

        AddColumn(grid, "Time", "Time", 190);
        AddColumn(grid, "Source", "Source", 130);
        AddColumn(grid, "Component", "Component", 130);
        AddColumn(grid, "Level", "Level", 85);
        AddColumn(grid, "Thread", "Thread", 150);
        AddColumn(grid, "Message", "Message", 400);

        grid.CellValueNeeded += OnCellValueNeeded;
        grid.SelectionChanged += OnSelectionChanged;
        grid.Scroll += OnGridScroll;
        return grid;
    }

    internal void SetPaused (bool isPaused, int backlogCount)
    {
        VerifyUiThread();
        IsPaused = isPaused;
        BacklogCount = Math.Max(0, backlogCount);
        UpdateLiveStateText();
    }

    private void OnFollowChanged (object? sender, EventArgs e)
    {
        UpdateLiveStateText();
        if (IsFollowEnabled && !IsPaused)
        {
            ScrollToTail();
        }

    }

    private void OnPauseClicked (object? sender, EventArgs e)
    {
        SetPaused(!IsPaused, IsPaused ? 0 : BacklogCount);
        PauseChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGridScroll (object? sender, ScrollEventArgs e)
    {
        if (_suppressFollowScrollTracking || e.ScrollOrientation != ScrollOrientation.VerticalScroll || _eventGrid.RowCount == 0)
        {
            return;
        }

        int firstDisplayedRow = _eventGrid.FirstDisplayedScrollingRowIndex;
        int displayedRows = _eventGrid.DisplayedRowCount(includePartialRow: false);
        _followCheckBox.Checked = firstDisplayedRow + displayedRows >= _eventGrid.RowCount;
    }

    private void ScrollToTail ()
    {
        if (_eventGrid.RowCount == 0)
        {
            return;
        }

        bool wasSuppressingScrollTracking = _suppressFollowScrollTracking;
        _suppressFollowScrollTracking = true;
        try
        {
            _eventGrid.FirstDisplayedScrollingRowIndex = _eventGrid.RowCount - 1;
        }
        finally
        {
            _suppressFollowScrollTracking = wasSuppressingScrollTracking;
        }
    }

    private void UpdateLiveStateText ()
    {
        _pauseButton.Text = Resources.ResourceManager.GetString(
            IsPaused ? "MinecraftWorkspace_Live_Resume" : "MinecraftWorkspace_Live_Pause",
            CultureInfo.CurrentUICulture)!;
        _liveStateLabel.Text = IsPaused
            ? string.Format(
                CultureInfo.CurrentCulture,
                Resources.ResourceManager.GetString("MinecraftWorkspace_Live_Paused", CultureInfo.CurrentUICulture)!,
                BacklogCount)
            : Resources.ResourceManager.GetString(
                IsFollowEnabled ? "MinecraftWorkspace_Live_Following" : "MinecraftWorkspace_Live_Anchored",
                CultureInfo.CurrentUICulture)!;
    }

    private static void AddColumn (DataGridView grid, string name, string header, int width)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private void SynchronizeEditor (MinecraftWorkspaceTimelineFilterQuery query)
    {
        _searchTextBox.Text = query.SearchText ?? string.Empty;
        _textScopeComboBox.SelectedIndex = (int)query.TextScope;
        _regexCheckBox.Checked = query.IsRegex;
        _caseSensitiveCheckBox.Checked = query.IsCaseSensitive;
        _invertCheckBox.Checked = query.IsInvert;

        _selectedFileIds.Clear();
        _selectedFileIds.UnionWith(query.FileIds);
        _selectedSources = new HashSet<MinecraftWorkspaceFacetValue<string>>(query.Sources);
        _selectedComponents = new HashSet<MinecraftWorkspaceFacetValue<string>>(query.Components);
        _selectedLevels = new HashSet<MinecraftWorkspaceFacetValue<LogLevel>>(query.Levels);
        _selectedThreads = new HashSet<MinecraftWorkspaceFacetValue<string>>(query.Threads);

        RenderFacetList(_fileFacetList, Snapshot.Facets.FileIds, _selectedFileIds, value => value);
        RenderFacetList(_sourceFacetList, Snapshot.Facets.Sources, _selectedSources, DisplayStringFacet);
        RenderFacetList(_componentFacetList, Snapshot.Facets.Components, _selectedComponents, DisplayStringFacet);
        RenderFacetList(_levelFacetList, Snapshot.Facets.Levels, _selectedLevels, DisplayLevelFacet);
        RenderFacetList(_threadFacetList, Snapshot.Facets.Threads, _selectedThreads, DisplayStringFacet);
    }

    private static void RenderFacetList<TValue> (
        CheckedListBox list,
        IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<TValue>> buckets,
        HashSet<TValue> selectedValues,
        Func<TValue, string> getLabel)
        where TValue : notnull
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (MinecraftWorkspaceFacetDisplayItem<TValue> bucket in buckets)
            {
                MinecraftWorkspaceFacetEditorItem<TValue> item = new(bucket, getLabel(bucket.Value));
                int index = list.Items.Add(item);
                list.SetItemChecked(index, selectedValues.Contains(item.Value));
            }
        }
        finally
        {
            list.EndUpdate();
        }
    }

    private void OnTextQueryChanged (object? sender, EventArgs e)
    {
        EmitQueryChanged();
    }

    private void OnClearFilters (object? sender, EventArgs e)
    {
        if (_synchronizingEditor)
        {
            return;
        }

        _synchronizingEditor = true;
        try
        {
            _searchTextBox.Clear();
            _textScopeComboBox.SelectedIndex = (int)MinecraftWorkspaceTextScope.Message;
            _regexCheckBox.Checked = false;
            _caseSensitiveCheckBox.Checked = false;
            _invertCheckBox.Checked = false;
            _selectedFileIds.Clear();
            _selectedSources.Clear();
            _selectedComponents.Clear();
            _selectedLevels.Clear();
            _selectedThreads.Clear();
            RenderFacetList(_fileFacetList, Snapshot.Facets.FileIds, _selectedFileIds, value => value);
            RenderFacetList(_sourceFacetList, Snapshot.Facets.Sources, _selectedSources, DisplayStringFacet);
            RenderFacetList(_componentFacetList, Snapshot.Facets.Components, _selectedComponents, DisplayStringFacet);
            RenderFacetList(_levelFacetList, Snapshot.Facets.Levels, _selectedLevels, DisplayLevelFacet);
            RenderFacetList(_threadFacetList, Snapshot.Facets.Threads, _selectedThreads, DisplayStringFacet);
        }
        finally
        {
            _synchronizingEditor = false;
        }

        EmitQueryChanged();
    }

    private void OnFacetItemCheck<TValue> (CheckedListBox list, HashSet<TValue> selectedValues, ItemCheckEventArgs e)
        where TValue : notnull
    {
        if (_synchronizingEditor)
        {
            return;
        }

        for (int index = 0; index < list.Items.Count; index++)
        {
            MinecraftWorkspaceFacetEditorItem<TValue> item = (MinecraftWorkspaceFacetEditorItem<TValue>)list.Items[index]!;
            selectedValues.Remove(item.Value);
        }

        for (int index = 0; index < list.Items.Count; index++)
        {
            bool isChecked = index == e.Index
                ? e.NewValue == CheckState.Checked
                : list.GetItemChecked(index);
            if (isChecked)
            {
                MinecraftWorkspaceFacetEditorItem<TValue> item = (MinecraftWorkspaceFacetEditorItem<TValue>)list.Items[index]!;
                selectedValues.Add(item.Value);
            }
        }

        EmitQueryChanged();
    }

    private void EmitQueryChanged ()
    {
        if (_synchronizingEditor)
        {
            return;
        }

        MinecraftWorkspaceTextScope textScope = _textScopeComboBox.SelectedIndex switch
        {
            (int)MinecraftWorkspaceTextScope.RawText => MinecraftWorkspaceTextScope.RawText,
            (int)MinecraftWorkspaceTextScope.MessageOrRawText => MinecraftWorkspaceTextScope.MessageOrRawText,
            _ => MinecraftWorkspaceTextScope.Message
        };
        MinecraftWorkspaceTimelineFilterQuery query = new(
            string.IsNullOrEmpty(_searchTextBox.Text) ? null : _searchTextBox.Text,
            _regexCheckBox.Checked,
            _caseSensitiveCheckBox.Checked,
            _invertCheckBox.Checked,
            textScope,
            _selectedFileIds,
            _selectedSources,
            _selectedComponents,
            _selectedLevels,
            _selectedThreads);
        FilterQueryChanged?.Invoke(this, new MinecraftWorkspaceFilterQueryChangedEventArgs(query));
    }

    private void OnCellValueNeeded (object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _snapshot.Rows.Count)
        {
            return;
        }

        MinecraftWorkspaceReadOnlyRow row = _snapshot.Rows[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => row.Time,
            1 => row.Source,
            2 => row.Component,
            3 => row.Level,
            4 => row.Thread,
            5 => row.Message,
            _ => null
        };
    }

    private void OnSelectionChanged (object? sender, EventArgs e)
    {
        if (_synchronizingEditor)
        {
            return;
        }

        if (_eventGrid.SelectedRows.Count != 1)
        {
            ClearSelectionAndDetails();
            return;
        }

        int selectedRowIndex = _eventGrid.SelectedRows[0].Index;
        if (selectedRowIndex < 0 || selectedRowIndex >= _snapshot.Rows.Count)
        {
            ClearSelectionAndDetails();
            return;
        }

        long identity = _snapshot.Rows[selectedRowIndex].Identity;
        if (!_rowIndexByIdentity.TryGetValue(identity, out int mappedRowIndex) || mappedRowIndex != selectedRowIndex)
        {
            ClearSelectionAndDetails();
            return;
        }

        SelectIdentity(identity);
    }

    private void SelectIdentity (long identity)
    {
        int rowIndex = _rowIndexByIdentity[identity];
        MinecraftWorkspaceReadOnlyRow row = Snapshot.Rows[rowIndex];
        SelectedIdentity = identity;
        SelectedEntry = row.Entry;
        SelectedDetails = row.Details;
        _detailsTextBox.Text = FormatDetails(row.Details);
    }

    private void ClearSelectionAndDetails ()
    {
        SelectedIdentity = null;
        SelectedEntry = null;
        SelectedDetails = null;
        _detailsTextBox.Clear();
    }

    private void RenderHeader ()
    {
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = Snapshot;
        _workspaceNameLabel.Text = snapshot.WorkspaceDisplayName;

        string matchedText = snapshot.MatchedCount?.ToString(CultureInfo.InvariantCulture) ?? "incomplete";
        _statusLabel.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.FilterStatus} · Loaded: {snapshot.TotalLoadedCount} · Matched: {matchedText}");
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorText))
        {
            _statusLabel.Text += $" · {snapshot.ErrorText}";
        }
    }

    private void VerifyUiThread ()
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId)
        {
            throw new InvalidOperationException();
        }
    }

    private static string DisplayStringFacet (MinecraftWorkspaceFacetValue<string> value) =>
        value.IsUnknown ? "Unknown" : value.Value;

    private static string DisplayLevelFacet (MinecraftWorkspaceFacetValue<LogLevel> value) =>
        value.IsUnknown ? "Unknown" : MinecraftWorkspaceReadOnlyDisplay.Level(value.Value);

    private static string FormatDetails (MinecraftWorkspaceReadOnlyEventDetails details)
    {
        StringBuilder text = new();
        AppendSection(text, "Message", details.Message);
        AppendSection(text, "RawText", details.RawText);
        text.AppendLine(CultureInfo.InvariantCulture, $"Physical path: {details.PhysicalPath}");
        text.AppendLine(CultureInfo.InvariantCulture, $"FileId: {details.FileId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Generation: {details.Generation}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Byte range: [{details.StartByteOffset}, {details.EndByteOffset})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Line range: [{details.StartLineNumber}, {details.EndLineNumber}]");
        text.AppendLine(CultureInfo.InvariantCulture, $"SourceLocalSequence: {details.SourceLocalSequence}");
        text.AppendLine(CultureInfo.InvariantCulture, $"ProducerSequence: {details.ProducerSequence?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Source timestamp raw: {details.Timestamp.RawValue ?? "Unknown"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Source timestamp value UTC: {FormatUtc(details.Timestamp.Value)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"TimestampBasis: {details.TimestampBasis}");
        text.AppendLine(CultureInfo.InvariantCulture, $"CandidateTimestampUtc: {FormatUtc(details.CandidateTimestampUtc)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"EffectiveTimestampUtc: {FormatUtc(details.EffectiveTimestampUtc)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"IngestedAtUtc: {FormatUtc(details.IngestedAtUtc)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"IsLate: {details.IsLate}");
        text.AppendLine(CultureInfo.InvariantCulture, $"WasTimestampAdjustedForSourceOrder: {details.WasTimestampAdjustedForSourceOrder}");
        text.AppendLine(CultureInfo.InvariantCulture, $"ParseStatus: {details.ParseStatus}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Source attribution: {FormatAttribution(details.SourceAttribution, value => value)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Component attribution: {FormatAttribution(details.ComponentAttribution, value => value)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Level attribution: {FormatAttribution(details.LevelAttribution, MinecraftWorkspaceReadOnlyDisplay.Level)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Thread attribution: {FormatAttribution(details.ThreadAttribution, value => value)}");
        return text.ToString();
    }

    private static void AppendSection (StringBuilder text, string title, string value)
    {
        text.Append(title).AppendLine(":").AppendLine(value);
    }

    private static string FormatUtc (DateTimeOffset? value) => value is DateTimeOffset timestamp
        ? MinecraftWorkspaceReadOnlyDisplay.UtcTime(timestamp)
        : "Unknown";

    private static string FormatAttribution<TValue> (AttributedValue<TValue> attribution, Func<TValue, string> display)
        where TValue : notnull => attribution.TryGetValue(out TValue? value)
            ? string.Create(CultureInfo.InvariantCulture, $"{display(value!)} ({attribution.Provenance}, {attribution.Confidence})")
            : string.Create(CultureInfo.InvariantCulture, $"Unknown ({attribution.Provenance}, {attribution.Confidence})");
}

/// <summary>A checked facet item that retains the exact YEE-50 display bucket and typed value.</summary>
public sealed class MinecraftWorkspaceFacetEditorItem<TValue>
{
    internal MinecraftWorkspaceFacetEditorItem (MinecraftWorkspaceFacetDisplayItem<TValue> displayItem, string valueLabel)
    {
        DisplayItem = displayItem;
        DisplayText = string.Create(
            CultureInfo.InvariantCulture,
            $"{valueLabel} · Total: {displayItem.TotalCount} · Matching: {displayItem.MatchingCount?.ToString(CultureInfo.InvariantCulture) ?? "incomplete"}");
    }

    public MinecraftWorkspaceFacetDisplayItem<TValue> DisplayItem { get; }

    public MinecraftWorkspaceFacetBucket<TValue> Bucket => DisplayItem.Bucket;

    public TValue Value => Bucket.Value;

    public string DisplayText { get; }

    public override string ToString () => DisplayText;
}

public sealed class MinecraftWorkspaceFilterQueryChangedEventArgs (MinecraftWorkspaceTimelineFilterQuery query) : EventArgs
{
    public MinecraftWorkspaceTimelineFilterQuery Query { get; } = query ?? throw new ArgumentNullException(nameof(query));
}
