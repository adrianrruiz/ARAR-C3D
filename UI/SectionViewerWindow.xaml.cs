using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using ARAR.Civil3D.Models;
using ARAR.Civil3D.Services;
using SectionGeometryModel = ARAR.Civil3D.Models.SectionGeometry;

namespace ARAR.Civil3D.UI;

/// <summary>Ventana modeless para consultar las secciones almacenadas en la caché de la sesión.</summary>
public partial class SectionViewerWindow : Window
{
    private readonly Document _document;
    private readonly CorridorSectionService _sectionService = new();
    private readonly TerrainSampler _terrainSampler = new();
    private IReadOnlyList<BaselineChoice> _baselineChoices;
    private IReadOnlyList<SurfaceChoice> _surfaceChoices;
    private CorridorSectionData? _data;
    private TerrainSectionResult? _terrain;
    private EarthworkResult? _earthwork;
    private bool _updatingSlider;
    private int _displayedIndex = -1;
    private bool _uiReady;
    private static int _stationFormatMode;
    private readonly Dictionary<int, TerrainSectionResult> _terrainCache = new();
    private ObjectId _terrainCacheSurfaceId;
    private readonly ObservableCollection<StakeRow> _stakeRows = new();
    private readonly ObservableCollection<PavementAreaRow> _pavementAreaRows = new();
    private readonly ObservableCollection<BookmarkRow> _bookmarkRows = new();
    private readonly BookmarkStore _bookmarkStore = new();

    public event EventHandler? ViewerClosed;
    public event Action<ObjectId, double>? SectionPositionChanged;
    public event EventHandler? CursorModeRequested;
    public event EventHandler? CenterRequested;
    public event Action<SectionDrawRequest>? DrawSectionRequested;

    public SectionViewerWindow(Document document, IReadOnlyList<BaselineChoice> baselines, IReadOnlyList<SurfaceChoice> surfaces)
    {
        InitializeComponent();
        _uiReady = true;
        _document = document;
        _baselineChoices = baselines;
        _surfaceChoices = surfaces;
        BaselineCombo.ItemsSource = baselines.Select(b => new BaselineDisplay(b, $"{b.CorridorName}  |  {b.BaselineName} — {b.AlignmentName}")).ToArray();
        SurfaceCombo.ItemsSource = surfaces;
        StakeGrid.ItemsSource = _stakeRows;
        PavementAreasGrid.ItemsSource = _pavementAreaRows;
        BookmarksList.ItemsSource = _bookmarkRows;
        PreviewKeyDown += Window_PreviewKeyDown;
        if (BaselineCombo.Items.Count > 0) BaselineCombo.SelectedIndex = 0;
        if (SurfaceCombo.Items.Count > 0) SurfaceCombo.SelectedIndex = 0;
        Closed += (_, _) => ViewerClosed?.Invoke(this, EventArgs.Empty);
    }

    public ObjectId? CurrentAlignmentId => _data?.Baseline.AlignmentId;
    public double? CurrentStation => _displayedIndex >= 0 && _data is not null ? _data.Cache.Sections[_displayedIndex].Station : null;

    public bool IsForDocument(Document document) => ReferenceEquals(_document, document);

    public void SetSectionByStation(double station)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_data is null || !IsLoaded) return;
            int index = _data.Cache.FindNearestIndex(station);
            if (index < 0 || index == _displayedIndex) return;
            StationSlider.Value = index;
        }));
    }

    private void NeighborOptions_Changed(object sender, RoutedEventArgs e) => UpdateNeighborDisplay();

    private void DisplayOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_uiReady) ApplyDisplayOptions();
    }

    private void ApplyDisplayOptions()
    {
        if (VerticalScaleCombo is null || ShowEarthworkBox is null || ShowShapesBox is null) return;
        double factor = VerticalScaleCombo.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 1.0;
        Canvas.SetDisplayOptions(ShowEarthworkBox.IsChecked == true, ShowShapesBox.IsChecked == true, factor);
        Canvas.SetBackground((BackgroundModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Dark");
    }

    private void CursorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null) { StatusText.Text = "Cargue primero las secciones."; return; }
        CursorModeRequested?.Invoke(this, EventArgs.Empty);
        StatusText.Text = "Modo cursor: mueva el puntero en planta; haga clic para fijar esa sección. Pulse Esc para salir.";
    }

    public IReadOnlyList<SectionDrawRequest> PrepareDrawRequests(DrawOptions options)
    {
        if (_data is null || _displayedIndex < 0 || _terrain is null || _earthwork is null) throw new InvalidOperationException("Cargue primero una sección.");
        if (!options.DrawRange) return new[] { new SectionDrawRequest(_data.Baseline, _data.Cache.Sections[_displayedIndex], _terrain.Geometry, _earthwork.Geometry) };
        if (SurfaceCombo.SelectedItem is not SurfaceChoice surface) throw new InvalidOperationException("Seleccione una superficie TIN.");
        double min = _data.Cache.Sections[0].Station, max = _data.Cache.Sections[^1].Station;
        if (options.RangeStart < min || options.RangeEnd > max || options.RangeStart > options.RangeEnd)
            throw new InvalidOperationException($"El rango debe estar entre {FormatStation(min)} y {FormatStation(max)}.");
        if ((options.RangeEnd - options.RangeStart) / options.RangeInterval > 5000) throw new InvalidOperationException("El rango produciría más de 5000 secciones; aumente el intervalo.");
        var indices = new SortedSet<int>();
        for (double station = options.RangeStart; station <= options.RangeEnd + 1e-8; station += options.RangeInterval)
            indices.Add(_data.Cache.FindNearestIndex(station));
        indices.Add(_data.Cache.FindNearestIndex(options.RangeEnd));
        var output = new List<SectionDrawRequest>(indices.Count);
        foreach (int index in indices)
        {
            CorridorSectionSnapshot section = _data.Cache.Sections[index];
            TerrainSectionResult terrain = index == _displayedIndex ? _terrain : _terrainSampler.Sample(_document, _data, surface, section);
            EarthworkResult earthwork = index == _displayedIndex ? _earthwork : EarthworkCalculator.Calculate(section.Geometry, terrain.Geometry);
            output.Add(new SectionDrawRequest(_data.Baseline, section, terrain.Geometry, earthwork.Geometry));
        }
        return output;
    }

    private void DrawSection_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _displayedIndex < 0 || _terrain is null || _earthwork is null) { StatusText.Text = "Cargue una sección y una superficie antes de dibujar."; return; }
        DrawSectionRequested?.Invoke(new SectionDrawRequest(_data.Baseline, _data.Cache.Sections[_displayedIndex], _terrain.Geometry, _earthwork.Geometry));
    }

    private void FitCanvas_Click(object sender, RoutedEventArgs e) => Canvas.FitToWindow();

    private void ExportImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = "Seccion.png", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        int resolution = ImageResolutionCombo.SelectedItem is ComboBoxItem scaleItem && int.TryParse(scaleItem.Tag?.ToString(), out int parsedScale) ? parsedScale : 2;
        var bitmap = RenderCanvasBitmap(resolution);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(dialog.FileName); encoder.Save(stream);
        StatusText.Text = "Imagen PNG exportada.";
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        try { int resolution = ImageResolutionCombo.SelectedItem is ComboBoxItem scaleItem && int.TryParse(scaleItem.Tag?.ToString(), out int parsedScale) ? parsedScale : 2;
        System.Windows.Clipboard.SetImage(RenderCanvasBitmap(resolution)); StatusText.Text = "Imagen copiada al portapapeles."; }
        catch (System.Exception ex) { StatusText.Text = "No se pudo copiar la imagen: " + ex.Message; }
    }

    private RenderTargetBitmap RenderCanvasBitmap(int resolutionScale)
    {
        int width = Math.Max(1, (int)Math.Ceiling(Canvas.ActualWidth * resolutionScale));
        int height = Math.Max(1, (int)Math.Ceiling(Canvas.ActualHeight * resolutionScale));
        var bitmap = new RenderTargetBitmap(width, height, 96 * resolutionScale, 96 * resolutionScale, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(Canvas); bitmap.Freeze(); return bitmap;
    }

    private void CenterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _displayedIndex < 0) { StatusText.Text = "Cargue primero las secciones."; return; }
        CenterRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BaselineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _data = null;
        _terrain = null;
        _earthwork = null;
        _terrainCache.Clear();
        _displayedIndex = -1;
        StationSlider.IsEnabled = false;
        _stakeRows.Clear();
        _pavementAreaRows.Clear();
        ConfigureProfilePanel(null);
        StakeGroup.Header = "Cartera de rasante";
        PavementTotalText.Text = "Total pavimento: 0.00 m²";
        _bookmarkRows.Clear();
        RefreshBookmarkTicks();
        Canvas.SetData(new SectionGeometryModel(), null, "");
        StatusText.Text = "Pulse «Cargar secciones» para preparar la caché de la baseline.";
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (BaselineCombo.SelectedItem is not BaselineDisplay baseline || SurfaceCombo.SelectedItem is not SurfaceChoice surface)
            {
                StatusText.Text = "Seleccione una baseline y una superficie TIN.";
                return;
            }
            LoadButton.IsEnabled = false;
            StatusText.Text = "Leyendo y almacenando las secciones calculadas…";
            _data = _sectionService.Load(_document, baseline.Choice);
            ConfigureProfilePanel(_data.Profile);
            _terrainCache.Clear();
            _terrainCacheSurfaceId = surface.SurfaceId;
            LoadBookmarks();
            _updatingSlider = true;
            StationSlider.Minimum = 0;
            StationSlider.Maximum = _data.Cache.Sections.Count - 1;
            StationSlider.Value = 0;
            StationSlider.IsEnabled = _data.Cache.Sections.Count > 1;
            _updatingSlider = false;
            RefreshBookmarkTicks();
            RefreshSection(0, surface);
            string loadStatus = _data.Profile is null
                ? "Aún no se ha calculado ningún perfil longitudinal para este eje."
                : $"Caché lista: {_data.Cache.Sections.Count:N0} secciones. La navegación usa PK calculados del corredor.";
            if (_data.AdjustedElevationFallbacks > 0)
                loadStatus += $" Se obtuvieron cotas de {_data.AdjustedElevationFallbacks:N0} secciones desde el perfil de la baseline.";
            if (_data.SkippedAssemblies > 0)
                loadStatus += $" Se omitieron {_data.SkippedAssemblies:N0} secciones sin cota de perfil válida.";
            StatusText.Text = loadStatus;
        }
        catch (System.Exception ex)
        {
            StatusText.Text = "No se pudieron cargar las secciones: " + ex.Message;
            Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nError al cargar las secciones: " + ex.Message);
        }
        finally { LoadButton.IsEnabled = true; }
    }

    private void StationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || _data is null || SurfaceCombo.SelectedItem is not SurfaceChoice surface) return;
        RefreshSection((int)Math.Round(StationSlider.Value), surface);
    }

    private void RefreshSection(int index, SurfaceChoice surface)
    {
        if (_data is null || index < 0 || index >= _data.Cache.Sections.Count) return;
        try
        {
            CorridorSectionSnapshot section = _data.Cache.Sections[index];
            _displayedIndex = index;
            _terrain = GetTerrainForIndex(index, surface);
            _earthwork = EarthworkCalculator.Calculate(section.Geometry, _terrain.Geometry);
            string station = FormatStation(section.Station);
            StationLabel.Text = "PK " + station;
            StakeGroup.Header = "Cartera de rasante - PK " + station;
            IReadOnlyList<SectionStakePoint> stakes = SectionReportCalculator.BuildStakePoints(
                section.Geometry, section.Segments, section.AxisElevation, _terrain.Geometry, _terrain.AxisElevation);
            Canvas.SetData(section.Geometry, _terrain.Geometry, "PK " + station, _earthwork.Geometry, section.Segments, stakes);
            ProfileCanvas.SetProfile(_data.Profile, section.Station, FormatStation);
            ApplyDisplayOptions();
            UpdateNeighborDisplay();
            SectionPositionChanged?.Invoke(_data.Baseline.AlignmentId, section.Station);
            CorridorText.Text = "Corredor: " + _data.Baseline.CorridorName;
            AlignmentText.Text = "Eje: " + _data.Baseline.AlignmentName;
            BaselineText.Text = "Baseline: " + _data.Baseline.BaselineName;
            RegionText.Text = "Región: " + section.RegionName;
            AssemblyText.Text = "Assembly aplicado: " + section.AssemblyName;
            ElevationText.Text = "Cota de proyecto en eje: " + FormatElevation(section.AxisElevation);
            TerrainText.Text = "Cota de terreno en eje: " + (_terrain.AxisElevation is double terrain ? FormatElevation(terrain) : "sin dato");
            CutFillText.Text = $"Desmonte: {_earthwork.CutAreaSquareMeters:0.00} m²\nTerraplén: {_earthwork.FillAreaSquareMeters:0.00} m²";
            DifferenceText.Text = _terrain.AxisElevation is double ground
                ? $"Diferencia proyecto - terreno: {section.AxisElevation - ground:+0.000;-0.000;0.000} m"
                : "Diferencia proyecto - terreno: sin dato";
            _stakeRows.Clear();
            foreach (SectionStakePoint stake in stakes)
                _stakeRows.Add(new StakeRow(stake.Number, stake.Description,
                    FormatNullable(stake.Offset), FormatNullable(stake.DesignElevation), FormatNullable(stake.GroundElevation),
                    stake.DesignElevation is double design && stake.GroundElevation is double stakeGround
                        ? (design - stakeGround).ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture)
                        : "-"));

            _pavementAreaRows.Clear();
            IReadOnlyList<PavementArea> areas = SectionReportCalculator.CalculatePavementAreas(section.Geometry);
            foreach (PavementArea area in areas)
                _pavementAreaRows.Add(new PavementAreaRow(PavementShapeCatalog.GetDisplayName(area.Code), area.Code,
                    area.LeftArea.ToString("0.00", CultureInfo.CurrentCulture), area.RightArea.ToString("0.00", CultureInfo.CurrentCulture),
                    area.TotalArea.ToString("0.00", CultureInfo.CurrentCulture), ToBrush(PavementShapeCatalog.GetColor(area.Code))));
            PavementTotalText.Text = $"Total pavimento: {areas.Sum(area => area.TotalArea):0.00} m²";
        }
        catch (System.Exception ex)
        {
            StatusText.Text = "Error al muestrear la superficie: " + ex.Message;
        }
    }

    private void GoStation_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null) { StatusText.Text = "Cargue primero las secciones."; return; }
        if (!TryParseStation(GoStationText.Text, out double station)) { StatusText.Text = "PK no válido. Use, por ejemplo, 1+250.000 o 1250."; return; }
        double min = _data.Cache.Sections[0].Station, max = _data.Cache.Sections[^1].Station;
        if (station < min || station > max) { StatusText.Text = $"El PK está fuera de la baseline ({FormatStation(min)} a {FormatStation(max)})."; return; }
        int index = _data.Cache.FindNearestIndex(station);
        StationSlider.Value = index;
        StatusText.Text = $"Sección calculada más cercana: PK {FormatStation(_data.Cache.Sections[index].Station)}.";
    }

    private static bool TryParseStation(string? text, out double station)
    {
        station = 0;
        string value = (text ?? string.Empty).Trim();
        if (value.StartsWith("PK", StringComparison.OrdinalIgnoreCase)) value = value[2..].Trim();
        int plus = value.IndexOf('+');
        if (plus >= 0 && value.IndexOf('+', plus + 1) < 0)
        {
            string kmText = value[..plus].Trim(), remainderText = value[(plus + 1)..].Trim();
            if (!double.TryParse(kmText, NumberStyles.Float, CultureInfo.InvariantCulture, out double km) ||
                !TryParseNumber(remainderText, out double remainder) || remainder >= 1000 || remainder < 0) return false;
            station = Math.Sign(km) * (Math.Abs(km) * 1000 + remainder);
            return double.IsFinite(station);
        }
        return TryParseNumber(value, out station) && double.IsFinite(station);
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private void LoadBookmarks()
    {
        if (_data is null) return;
        try
        {
            _bookmarkRows.Clear();
            foreach (SectionBookmark bookmark in _bookmarkStore.Load(_document, _data.Baseline)) _bookmarkRows.Add(new BookmarkRow(bookmark));
            RefreshBookmarkTicks();
        }
        catch (System.Exception ex) { AddinLogger.Error("No se pudieron leer los favoritos del DWG.", ex); StatusText.Text = "No se pudieron leer los favoritos: " + ex.Message; }
    }

    private void SaveBookmark_Click(object sender, RoutedEventArgs e)
    {
        double? station = CurrentStation;
        if (_data is null || station is null) { StatusText.Text = "Cargue una sección antes de guardar un favorito."; return; }
        string label = (BookmarkLabelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Revisar";
        _bookmarkRows.Add(new BookmarkRow(new SectionBookmark(station.Value, BookmarkNoteBox.Text.Trim(), label, DateTime.UtcNow)));
        RefreshBookmarkTicks();
        PersistBookmarks();
    }

    private void BookmarksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BookmarksList.SelectedItem is BookmarkRow row)
        {
            BookmarkNoteBox.Text = row.Bookmark.Note;
            BookmarkLabelCombo.SelectedIndex = row.Bookmark.Label switch { "Conflicto" => 1, "Aprobada" => 2, _ => 0 };
        }
    }

    private void GoBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarksList.SelectedItem is BookmarkRow row) GoToStation(row.Bookmark.Station);
    }

    private void UpdateBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarksList.SelectedItem is not BookmarkRow selected) return;
        int index = _bookmarkRows.IndexOf(selected);
        string label = (BookmarkLabelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? selected.Bookmark.Label;
        _bookmarkRows[index] = new BookmarkRow(selected.Bookmark with { Note = BookmarkNoteBox.Text.Trim(), Label = label });
        PersistBookmarks();
    }

    private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarksList.SelectedItem is not BookmarkRow selected) return;
        _bookmarkRows.Remove(selected);
        RefreshBookmarkTicks();
        PersistBookmarks();
    }

    private void PreviousBookmark_Click(object sender, RoutedEventArgs e) => GoToRelativeBookmark(false);
    private void NextBookmark_Click(object sender, RoutedEventArgs e) => GoToRelativeBookmark(true);

    private void GoToRelativeBookmark(bool next)
    {
        double? station = CurrentStation;
        if (station is null || _bookmarkRows.Count == 0) return;
        IEnumerable<SectionBookmark> ordered = _bookmarkRows.Select(x => x.Bookmark).OrderBy(x => x.Station);
        SectionBookmark? target = next ? ordered.FirstOrDefault(x => x.Station > station.Value + 1e-6) : ordered.LastOrDefault(x => x.Station < station.Value - 1e-6);
        if (target is not null) GoToStation(target.Station);
    }

    private void GoToStation(double station)
    {
        if (_data is null || station < _data.Cache.Sections[0].Station || station > _data.Cache.Sections[^1].Station) return;
        GoStationText.Text = FormatStation(station);
        StationSlider.Value = _data.Cache.FindNearestIndex(station);
    }

    private void PersistBookmarks()
    {
        if (_data is null) return;
        try { _bookmarkStore.Save(_document, _data.Baseline, _bookmarkRows.Select(x => x.Bookmark).ToArray()); StatusText.Text = "Favoritos guardados en el DWG."; }
        catch (System.Exception ex) { AddinLogger.Error("No se pudieron guardar los favoritos en el DWG.", ex); StatusText.Text = "No se pudieron escribir en el DWG; los favoritos siguen en memoria. " + ex.Message; }
    }

    private void ExportBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "FavoritosSecciones.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var csv = new StringBuilder("PK,Nota,Etiqueta,FechaUTC\r\n");
        foreach (SectionBookmark b in _bookmarkRows.Select(x => x.Bookmark).OrderBy(x => x.Station))
            csv.Append(b.Station.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(CsvEscape(b.Note)).Append(',').Append(CsvEscape(b.Label)).Append(',').Append(b.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)).Append("\r\n");
        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
        StatusText.Text = "Favoritos exportados a CSV.";
    }

    private void ImportBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            foreach (string line in File.ReadLines(dialog.FileName).Skip(1))
            {
                List<string> fields = ParseCsvLine(line);
                if (fields.Count < 3 || !TryParseNumber(fields[0], out double station) || !double.IsFinite(station)) continue;
                DateTime created = fields.Count > 3 && DateTime.TryParse(fields[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed) ? parsed : DateTime.UtcNow;
                _bookmarkRows.Add(new BookmarkRow(new SectionBookmark(station, fields[1], fields[2], created.ToUniversalTime())));
            }
            RefreshBookmarkTicks();
            PersistBookmarks();
        }
        catch (System.Exception ex) { StatusText.Text = "No se pudo importar el CSV: " + ex.Message; }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        if (e.Key == System.Windows.Input.Key.F6) { GoToRelativeBookmark(true); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.F5) { GoToRelativeBookmark(false); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Left) { MoveSection(-1); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Right) { MoveSection(1); e.Handled = true; }
    }

    private void MoveSection(int direction)
    {
        if (_data is null || _displayedIndex < 0) return;
        int step = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 ? 10 : 1;
        StationSlider.Value = Math.Clamp(_displayedIndex + direction * step, 0, _data.Cache.Sections.Count - 1);
    }

    private void UpdateNeighborDisplay()
    {
        if (_data is null || _displayedIndex < 0) { Canvas.SetNeighborSections(Array.Empty<NeighborSectionGeometry>(), false, false); return; }
        int count = NeighborCountCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int parsed) ? parsed : 1;
        bool showPrevious = NeighborPreviousBox.IsChecked == true, showNext = NeighborNextBox.IsChecked == true;
        var neighbors = new List<NeighborSectionGeometry>();
        SurfaceChoice? surface = SurfaceCombo.SelectedItem as SurfaceChoice;
        for (int distance = 1; distance <= count; distance++)
        {
            AddNeighbor(_displayedIndex - distance, true, showPrevious || NeighborTerrainBox.IsChecked == true, surface, neighbors);
            AddNeighbor(_displayedIndex + distance, false, showNext || NeighborTerrainBox.IsChecked == true, surface, neighbors);
        }
        Canvas.SetNeighborSections(neighbors, showPrevious, showNext);
        var diffs = new List<string>();
        if (_displayedIndex > 0) diffs.Add("Δ vs. anterior: " + FormatDifferences(_data.Cache.Sections[_displayedIndex], _data.Cache.Sections[_displayedIndex - 1]));
        if (_displayedIndex + 1 < _data.Cache.Sections.Count) diffs.Add("Δ vs. siguiente: " + FormatDifferences(_data.Cache.Sections[_displayedIndex], _data.Cache.Sections[_displayedIndex + 1]));
        NeighborDifferenceText.Text = string.Join("\n", diffs);
    }

    private void AddNeighbor(int index, bool previous, bool includeTerrain, SurfaceChoice? surface, List<NeighborSectionGeometry> output)
    {
        if (_data is null || index < 0 || index >= _data.Cache.Sections.Count) return;
        CorridorSectionSnapshot section = _data.Cache.Sections[index];
        var geometry = new SectionGeometryModel();
        geometry.Polylines.AddRange(section.Geometry.Polylines);
        if (includeTerrain && surface is not null) geometry.Polylines.AddRange(GetTerrainForIndex(index, surface).Geometry.Polylines);
        output.Add(new NeighborSectionGeometry(geometry, section.Station, previous));
    }

    private TerrainSectionResult GetTerrainForIndex(int index, SurfaceChoice surface)
    {
        if (_data is null) throw new InvalidOperationException("La caché de secciones no está cargada.");
        if (_terrainCacheSurfaceId != surface.SurfaceId) { _terrainCache.Clear(); _terrainCacheSurfaceId = surface.SurfaceId; }
        if (!_terrainCache.TryGetValue(index, out TerrainSectionResult? result))
        {
            result = _terrainSampler.Sample(_document, _data, surface, _data.Cache.Sections[index]);
            _terrainCache[index] = result;
        }
        return result;
    }

    private void StationFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || StationFormatCombo.SelectedItem is not ComboBoxItem item || !int.TryParse(item.Tag?.ToString(), out _stationFormatMode)) return;
        BookmarksList.Items.Refresh();
        if (_displayedIndex >= 0 && SurfaceCombo.SelectedItem is SurfaceChoice surface) RefreshSection(_displayedIndex, surface);
    }

    private void SurfaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _data is null || SurfaceCombo.SelectedItem is not SurfaceChoice surface) return;
        _terrainCache.Clear(); _terrainCacheSurfaceId = surface.SurfaceId;
        if (_displayedIndex >= 0) RefreshSection(_displayedIndex, surface);
    }

    private void RefreshBookmarkTicks()
    {
        if (StationSlider is null || _data is null) return;
        var ticks = new System.Windows.Media.DoubleCollection();
        foreach (BookmarkRow row in _bookmarkRows) ticks.Add(_data.Cache.FindNearestIndex(row.Bookmark.Station));
        StationSlider.Ticks = ticks;
    }

    private static string FormatDifferences(CorridorSectionSnapshot current, CorridorSectionSnapshot other)
    {
        double widthA = current.MaximumOffset - current.MinimumOffset, widthB = other.MaximumOffset - other.MinimumOffset;
        double leftA = AverageSlope(current, "Izquierda"), leftB = AverageSlope(other, "Izquierda");
        double rightA = AverageSlope(current, "Derecha"), rightB = AverageSlope(other, "Derecha");
        return $"Eje {current.AxisElevation - other.AxisElevation:+0.000;-0.000;0.000} m; ancho {widthA - widthB:+0.000;-0.000;0.000} m; ptes. I/D {leftA-leftB:+0.00;-0.00;0.00}/{rightA-rightB:+0.00;-0.00;0.00} %";
    }

    private static double AverageSlope(CorridorSectionSnapshot section, string side) => section.Segments.Where(s => s.Side == side && (s.LinkCode.Contains("TOP", StringComparison.OrdinalIgnoreCase) || s.LinkCode.Contains("PAVE", StringComparison.OrdinalIgnoreCase) || s.LinkCode.Contains("CROWN", StringComparison.OrdinalIgnoreCase))).Select(s => s.SlopePercent).DefaultIfEmpty(0).Average();

    private static string CsvEscape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>(); var field = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\"' && quoted && i + 1 < line.Length && line[i + 1] == '\"') { field.Append('\"'); i++; }
            else if (c == '\"') quoted = !quoted;
            else if (c == ',' && !quoted) { values.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        values.Add(field.ToString()); return values;
    }

    private sealed record BookmarkRow(SectionBookmark Bookmark)
    {
        public string Display => $"PK {FormatStation(Bookmark.Station)} · {Bookmark.Label} · {Bookmark.Note}";
    }

    private static string FormatStation(double station)
    {
        if (!double.IsFinite(station)) return "PK no válido";
        bool negative = station < 0; double absolute = Math.Abs(station);
        if (_stationFormatMode == 2) return station.ToString("0.000", CultureInfo.CurrentCulture);
        long km = (long)Math.Floor(absolute / 1000.0); double remainder = absolute - km * 1000.0;
        string pattern = _stationFormatMode == 1 ? "000.00" : "000.000";
        return (negative ? "-" : "") + km.ToString(CultureInfo.InvariantCulture) + "+" + remainder.ToString(pattern, CultureInfo.CurrentCulture);
    }

    private static string FormatElevation(double elevation) => elevation.ToString("0.000 m", CultureInfo.CurrentCulture);
    private sealed record BaselineDisplay(BaselineChoice Choice, string Display);
    private void ConfigureProfilePanel(LongitudinalProfileSnapshot? profile)
    {
        bool available = profile is not null;
        ProfileCanvas.Visibility = available ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ProfileSplitter.Visibility = available ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ProfileChartRow.Height = available ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        ProfileSplitterRow.Height = available ? new GridLength(5) : new GridLength(0);
        SectionChartRow.Height = available ? new GridLength(3, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        if (!available) ProfileCanvas.SetProfile(null, double.NaN, FormatStation);
    }

    private static string FormatNullable(double? value) => value is double number
        ? number.ToString("0.000", CultureInfo.CurrentCulture)
        : "-";

    private static System.Windows.Media.Brush ToBrush(RgbaColor color) =>
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));

    private sealed record StakeRow(string Number, string Description, string OffsetText, string DesignText, string GroundText, string DifferenceText);
    private sealed record PavementAreaRow(string Name, string Code, string LeftText, string RightText, string TotalText, System.Windows.Media.Brush Swatch);
}
