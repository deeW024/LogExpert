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
    private readonly TreeView _facetTree;
    private readonly DataGridView _eventGrid;
    private readonly TextBox _detailsTextBox;
    private IReadOnlyDictionary<long, int> _rowIndexByIdentity = new Dictionary<long, int>();
    private MinecraftWorkspaceReadOnlyViewSnapshot _snapshot;
    private bool _applyingSnapshot;

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
        var header = new Panel { Name = "WorkspaceHeader", Dock = DockStyle.Top, Height = 56 };
        header.Controls.Add(_statusLabel);
        header.Controls.Add(_workspaceNameLabel);

        _facetTree = new TreeView
        {
            Name = "WorkspaceFacetSummary",
            Dock = DockStyle.Fill,
            HideSelection = false
        };

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

        var contentSplit = new SplitContainer
        {
            Name = "WorkspaceContentSplit",
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Size = new Size(960, 584),
            SplitterDistance = 250,
            Panel1MinSize = 180,
            Panel2MinSize = 320
        };
        contentSplit.Panel1.Controls.Add(_facetTree);

        var eventSplit = new SplitContainer
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

        Controls.Add(contentSplit);
        Controls.Add(header);

        ApplySnapshot(snapshot);
    }

    public MinecraftWorkspaceReadOnlyViewSnapshot Snapshot => _snapshot;

    public DataGridView EventGrid => _eventGrid;

    public TreeView FacetTree => _facetTree;

    public TextBox DetailsTextBox => _detailsTextBox;

    public Label StatusLabel => _statusLabel;

    public long? SelectedIdentity { get; private set; }

    public MinecraftWorkspaceTimelineEntry? SelectedEntry { get; private set; }

    public MinecraftWorkspaceReadOnlyEventDetails? SelectedDetails { get; private set; }

    /// <summary>Rebinds a completed presenter snapshot while preserving selection by timeline Identity.</summary>
    public void ApplySnapshot (MinecraftWorkspaceReadOnlyViewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        VerifyUiThread();

        long? previouslySelectedIdentity = SelectedIdentity;
        _applyingSnapshot = true;
        try
        {
            _eventGrid.ClearSelection();
            _eventGrid.CurrentCell = null;
            _eventGrid.RowCount = 0;

            _snapshot = snapshot;
            _rowIndexByIdentity = snapshot.Rows
                .Select((row, index) => (row.Identity, index))
                .ToDictionary(pair => pair.Identity, pair => pair.index);

            RenderHeader();
            RenderFacets();
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
        }
        finally
        {
            _applyingSnapshot = false;
        }
    }

    private DataGridView CreateGrid ()
    {
        var grid = new DataGridView
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
        return grid;
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
        if (_applyingSnapshot)
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

    private void RenderFacets ()
    {
        _facetTree.BeginUpdate();
        try
        {
            _facetTree.Nodes.Clear();
            AddFacetGroup("File", Snapshot.Facets.FileIds);
            AddFacetGroup("Source", Snapshot.Facets.Sources);
            AddFacetGroup("Component", Snapshot.Facets.Components);
            AddFacetGroup("Level", Snapshot.Facets.Levels);
            AddFacetGroup("Thread", Snapshot.Facets.Threads);
            _facetTree.ExpandAll();
        }
        finally
        {
            _facetTree.EndUpdate();
        }
    }

    private void AddFacetGroup<TValue> (string groupName, IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<TValue>> items)
    {
        var group = new TreeNode(groupName) { Name = groupName };
        foreach (MinecraftWorkspaceFacetDisplayItem<TValue> item in items)
        {
            string matching = item.MatchingCount?.ToString(CultureInfo.InvariantCulture) ?? "incomplete";
            group.Nodes.Add(new TreeNode(string.Create(
                CultureInfo.InvariantCulture,
                $"{item.DisplayLabel} · Total: {item.TotalCount} · Matching: {matching}")));
        }

        _facetTree.Nodes.Add(group);
    }

    private void VerifyUiThread ()
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId)
        {
            throw new InvalidOperationException();
        }
    }

    private static string FormatDetails (MinecraftWorkspaceReadOnlyEventDetails details)
    {
        var text = new StringBuilder();
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
