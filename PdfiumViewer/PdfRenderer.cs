using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PdfiumViewer
{
    /// <summary>
    /// Control to render PDF documents.
    /// </summary>
    public class PdfRenderer : PanningZoomingScrollControl
    {
        private static readonly Padding PageMargin = new Padding(4);
        private static readonly SolidBrush _textSelectionBrush = new SolidBrush(Color.FromArgb(90, Color.DodgerBlue));

        private int _maxWidth;
        private int _maxHeight;
        private double _documentScaleFactor;
        private bool _disposed;
        private double _scaleFactor;
        private ShadeBorder _shadeBorder = new ShadeBorder();
        private int _suspendPaintCount;
        private ToolTip _toolTip;
        private PdfViewerZoomMode _zoomMode;
        private bool _pageCacheValid;
        private readonly List<PageCache> _pageCache = new List<PageCache>();
        private int _visiblePageStart;
        private int _visiblePageEnd;
        private PageLink _cachedLink;
        private DragState _dragState;
        private PdfRotation _rotation;
        private List<IPdfMarker>[] _markers;
        private PdfViewerCursorMode _cursorMode = PdfViewerCursorMode.Pan;
        private bool _isSelectingText = false;
        private MouseState _cachedMouseState = null;
        private TextSelectionState _textSelectionState = null;
        private PdfViewerInteractionMode _mode = PdfViewerInteractionMode.None;
        private double _dominantPageShare = 0.1;
        private int _currentPage = -1;

        private Point _rbStart;
        private Rectangle _rbRect;
        private Rectangle _rbPrevRect;

        private Point _panStart;
        private Point _panDisplayStart;
        /// <summary>
        /// The associated PDF document.
        /// </summary>
        public IPdfDocument Document { get; private set; }


        [DefaultValue(true)]
        public bool AutoCopyTextSelectionToClipboard { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the user can give the focus to this control using the TAB key.
        /// </summary>
        /// 
        /// <returns>
        /// true if the user can give the focus to the control using the TAB key; otherwise, false. The default is true.Note:This property will always return true for an instance of the <see cref="T:System.Windows.Forms.Form"/> class.
        /// </returns>
        /// <filterpriority>1</filterpriority><PermissionSet><IPermission class="System.Security.Permissions.EnvironmentPermission, mscorlib, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" version="1" Unrestricted="true"/><IPermission class="System.Security.Permissions.FileIOPermission, mscorlib, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" version="1" Unrestricted="true"/><IPermission class="System.Security.Permissions.SecurityPermission, mscorlib, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" version="1" Flags="UnmanagedCode, ControlEvidence"/><IPermission class="System.Diagnostics.PerformanceCounterPermission, System, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" version="1" Unrestricted="true"/></PermissionSet>
        [DefaultValue(true)]
        public new bool TabStop
        {
            get { return base.TabStop; }
            set { base.TabStop = value; }
        }

        /// <summary>
        /// Gets or sets the minimum lead over the next most visible page (as a share of the viewport)
        /// required for a page to be treated as dominant when determining the current page.
        /// Values are clamped between 0.0 and 1.0.
        /// </summary>
        [DefaultValue(0.1)]
        public double DominantPageShare
        {
            get { return _dominantPageShare; }
            set { _dominantPageShare = Clamp(value, 0.0, 1.0); }
        }

        /// <summary>
        /// Gets or sets the currently focused page.
        /// </summary>
        public int Page
        {
            get
            {
                if (Document == null || !_pageCacheValid)
                    return 0;

                int top = -DisplayRectangle.Top;
                int bottom = top + GetScrollClientArea().Height;

                int bestPage = 0;
                int bestVisible = -1;
                int secondBestVisible = -1;
                int viewportHeight = GetScrollClientArea().Height;

                for (int page = 0; page < Document.PageSizes.Count; page++)
                {
                    var pageCache = _pageCache[page].OuterBounds;
                    int visible = Math.Max(0, Math.Min(bottom, pageCache.Bottom) - Math.Max(top, pageCache.Top));

                    if (visible > bestVisible)
                    {
                        secondBestVisible = bestVisible;
                        bestVisible = visible;
                        bestPage = page;
                    }
                    else if (visible > secondBestVisible)
                    {
                        secondBestVisible = visible;
                    }
                }

                if (viewportHeight > 0 && bestVisible >= 0)
                {
                    double leadShare = (double)(bestVisible - Math.Max(0, secondBestVisible)) / viewportHeight;
                    if (leadShare >= _dominantPageShare)
                        _currentPage = bestPage;
                }

                if (_currentPage < 0)
                    _currentPage = bestPage;

                return _currentPage;
            }
            set
            {
                if (Document == null)
                {
                    SetDisplayRectLocation(new Point(0, 0));
                }
                else
                {
                    int page = Math.Min(Math.Max(value, 0), Document.PageCount - 1);

                    SetDisplayRectLocation(new Point(0, -_pageCache[page].OuterBounds.Top));
                    _currentPage = page;
                }
            }
        }

        public Point GetDisplayLocation()
        {
            return DisplayRectangle.Location;
        }

        public void SetDisplayLocation(Point location)
        {
            // location è in coordinate DisplayRectangle (di solito Y negativa quando scrolli in giù)
            SetDisplayRectLocation(location);
        }

        public Rectangle GetDisplayRect()
        {
            return DisplayRectangle;
        }


        /// <summary>
        /// Get the outer bounds of the page.
        /// </summary>
        /// <param name="page">The page to get the bounds for.</param>
        /// <returns>The bounds of the page.</returns>
        public Rectangle GetOuterBounds(int page)
        {
            if (Document == null || !_pageCacheValid)
                return Rectangle.Empty;

            page = Math.Min(Math.Max(page, 0), Document.PageCount - 1);
            return _pageCache[page].OuterBounds;
        }

        /// <summary>
        /// Gets or sets the way the document should be zoomed initially.
        /// </summary>
        public PdfViewerZoomMode ZoomMode
        {
            get { return _zoomMode; }
            set
            {
                _zoomMode = value;
                PerformLayout();
            }
        }

        /// <summary>
        /// Gets or sets the way the viewer should respond to cursor input
        /// </summary>
        [DefaultValue(PdfViewerCursorMode.Pan)]
        public PdfViewerCursorMode CursorMode
        {
            get { return _cursorMode; }
            set
            {
                _cursorMode = value;
                //MousePanningEnabled = _cursorMode == PdfViewerCursorMode.Pan; //original verson
                // IMPORTANT: disabilitiamo il panning automatico del base control.
                // Il pan lo gestiamo SOLO col tasto destro.
                MousePanningEnabled = false;
            }
        }

        /// <summary>
        /// Gets or sets the current rotation of the PDF document.
        /// </summary>
        public PdfRotation Rotation
        {
            get { return _rotation; }
            set
            {
                if (_rotation != value)
                {
                    _rotation = value;
                    ResetFromRotation();
                }
            }
        }

        /// <summary>
        /// Indicates whether the user currently has text selected
        /// </summary>
        public bool IsTextSelected
        {
            get
            {
                var state = _textSelectionState?.GetNormalized();
                if (state == null)
                    return false;

                if (state.EndPage < 0 || state.EndIndex < 0)
                    return false;

                return true;
            }
        }

        /// <summary>
        /// Gets the currently selected text
        /// </summary>
        public string SelectedText
        {
            get
            {
                var state = _textSelectionState?.GetNormalized();
                if (state == null)
                    return null;

                var sb = new StringBuilder();
                for (int page = state.StartPage; page <= state.EndPage; page++)
                {
                    int start = 0, end = 0;

                    if (page == state.StartPage)
                        start = state.StartIndex;

                    if (page == state.EndPage)
                        end = (state.EndIndex + 1);
                    else
                        end = Document.CountCharacters(page);

                    if (page != state.StartPage)
                        sb.AppendLine();

                    sb.Append(Document.GetPdfText(new PdfTextSpan(page, start, end - start)));
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Gets a collection with all markers.
        /// </summary>
        public PdfMarkerCollection Markers { get; }

        /// <summary>
        /// Initializes a new instance of the PdfRenderer class.
        /// </summary>
        public PdfRenderer()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);

            TabStop = true;

            _toolTip = new ToolTip();

            Markers = new PdfMarkerCollection();
            Markers.CollectionChanged += Markers_CollectionChanged;
        }

        private void Markers_CollectionChanged(object sender, EventArgs e)
        {
            RedrawMarkers();
        }

        /// <summary>
        /// Converts client coordinates to PDF coordinates.
        /// </summary>
        /// <param name="location">Client coordinates to get the PDF location for.</param>
        /// <returns>The location in a PDF page or a PdfPoint with IsValid false when the coordinates do not match a PDF page.</returns>
        public PdfPoint PointToPdf(Point location)
        {
            if (Document == null)
                return PdfPoint.Empty;

            var offset = GetScrollOffset();
            location.Offset(-offset.Width, -offset.Height);

            for (int page = 0; page < Document.PageSizes.Count; page++)
            {
                var pageCache = _pageCache[page];
                if (pageCache.OuterBounds.Contains(location))
                {
                    if (pageCache.Bounds.Contains(location))
                    {
                        location = new Point(
                            location.X - pageCache.Bounds.X,
                            location.Y - pageCache.Bounds.Y
                        );
                        var translated = TranslatePointToPdf(pageCache.Bounds.Size, Document.PageSizes[page], location);
                        translated = Document.PointToPdf(page, new Point((int)translated.X, (int)translated.Y));

                        return new PdfPoint(page, translated);
                    }

                    break;
                }
            }

            return PdfPoint.Empty;
        }

        /// <summary>
        /// Converts a PDF point to a client point.
        /// </summary>
        /// <param name="point">The PDF point to convert.</param>
        /// <returns>The location of the point in client coordinates.</returns>
        public Point PointFromPdf(PdfPoint point)
        {
            var offset = GetScrollOffset();
            var pageBounds = _pageCache[point.Page].Bounds;

            var translated = Document.PointFromPdf(point.Page, point.Location);
            var location = TranslatePointFromPdf(pageBounds.Size, Document.PageSizes[point.Page], translated);

            return new Point(
                pageBounds.Left + offset.Width + location.X,
                pageBounds.Top + offset.Height + location.Y
            );
        }

        /// <summary>
        /// Converts client coordinates to PDF bounds.
        /// </summary>
        /// <param name="bounds">The client coordinates to convert.</param>
        /// <returns>The PDF bounds.</returns>
        public PdfRectangle BoundsToPdf(Rectangle bounds)
        {
            if (Document == null)
                return PdfRectangle.Empty;

            var offset = GetScrollOffset();
            bounds.Offset(-offset.Width, -offset.Height);

            for (int page = 0; page < Document.PageSizes.Count; page++)
            {
                var pageCache = _pageCache[page];
                if (pageCache.OuterBounds.Contains(bounds.Location))
                {
                    if (pageCache.Bounds.Contains(bounds.Location))
                    {
                        var topLeft = new Point(
                            bounds.Left - pageCache.Bounds.Left,
                            bounds.Top - pageCache.Bounds.Top
                        );
                        var bottomRight = new Point(
                            bounds.Right - pageCache.Bounds.Left,
                            bounds.Bottom - pageCache.Bounds.Top
                        );

                        var translatedTopLeft = TranslatePointToPdf(pageCache.Bounds.Size, Document.PageSizes[page], topLeft);
                        var translatedBottomRight = TranslatePointToPdf(pageCache.Bounds.Size, Document.PageSizes[page], bottomRight);

                        var translated = Document.RectangleToPdf(
                            page,
                            new Rectangle(
                                (int)translatedTopLeft.X,
                                (int)translatedTopLeft.Y,
                                (int)(translatedBottomRight.X - translatedTopLeft.X),
                                (int)(translatedBottomRight.Y - translatedTopLeft.Y)
                            )
                        );

                        return new PdfRectangle(page, translated);
                    }

                    break;
                }
            }

            return PdfRectangle.Empty;
        }

        /// <summary>
        /// Converts PDF bounds to client bounds.
        /// </summary>
        /// <param name="bounds">The PDF bounds to convert.</param>
        /// <returns>The bounds of the PDF bounds in client coordinates.</returns>
        public Rectangle BoundsFromPdf(PdfRectangle bounds)
        {
            return BoundsFromPdf(bounds, true);
        }

        private Rectangle BoundsFromPdf(PdfRectangle bounds, bool translateOffset)
        {
            var offset = translateOffset ? GetScrollOffset() : Size.Empty;
            var pageBounds = _pageCache[bounds.Page].Bounds;
            var pageSize = Document.PageSizes[bounds.Page];

            var translated = Document.RectangleFromPdf(
                bounds.Page,
                bounds.Bounds
            );

            var topLeft = TranslatePointFromPdf(pageBounds.Size, pageSize, new PointF(translated.Left, translated.Top));
            var bottomRight = TranslatePointFromPdf(pageBounds.Size, pageSize, new PointF(translated.Right, translated.Bottom));

            return new Rectangle(
                pageBounds.Left + offset.Width + Math.Min(topLeft.X, bottomRight.X),
                pageBounds.Top + offset.Height + Math.Min(topLeft.Y, bottomRight.Y),
                Math.Abs(bottomRight.X - topLeft.X),
                Math.Abs(bottomRight.Y - topLeft.Y)
            );
        }

        private PointF TranslatePointToPdf(Size size, SizeF pageSize, Point point)
        {
            switch (Rotation)
            {
                case PdfRotation.Rotate90:
                    point = new Point(size.Height - point.Y, point.X);
                    size = new Size(size.Height, size.Width);
                    break;
                case PdfRotation.Rotate180:
                    point = new Point(size.Width - point.X, size.Height - point.Y);
                    break;
                case PdfRotation.Rotate270:
                    point = new Point(point.Y, size.Width - point.X);
                    size = new Size(size.Height, size.Width);
                    break;
            }

            return new PointF(
                ((float)point.X / size.Width) * pageSize.Width,
                ((float)point.Y / size.Height) * pageSize.Height
            );
        }

        private Point TranslatePointFromPdf(Size size, SizeF pageSize, PointF point)
        {
            switch (Rotation)
            {
                case PdfRotation.Rotate90:
                    point = new PointF(pageSize.Height - point.Y, point.X);
                    pageSize = new SizeF(pageSize.Height, pageSize.Width);
                    break;
                case PdfRotation.Rotate180:
                    point = new PointF(pageSize.Width - point.X, pageSize.Height - point.Y);
                    break;
                case PdfRotation.Rotate270:
                    point = new PointF(point.Y, pageSize.Width - point.X);
                    pageSize = new SizeF(pageSize.Height, pageSize.Width);
                    break;
            }

            return new Point(
                (int)((point.X / pageSize.Width) * size.Width),
                (int)((point.Y / pageSize.Height) * size.Height)
            );
        }
        public void ZoomToClientRectangle(Rectangle selectionClient)
        {
            if (Document == null || !_pageCacheValid)
                return;

            selectionClient = NormalizeRect(selectionClient);

            // Scarta selezioni troppo piccole
            if (selectionClient.Width < 6 || selectionClient.Height < 6)
                return;

            // Area visibile reale (senza scrollbar)
            var clientArea = GetScrollClientArea();
            if (clientArea.Width <= 0 || clientArea.Height <= 0)
                return;

            // Limita la selezione all'area visibile
            var sel = Rectangle.Intersect(selectionClient, clientArea);
            if (sel.Width < 6 || sel.Height < 6)
                return;

            // Centro della selezione (in client)
            var selCenterClient = new Point(sel.Left + sel.Width / 2, sel.Top + sel.Height / 2);

            // Punto PDF sotto il centro selezionato (STABILE) PRIMA dello zoom
            var pdfCenter = PointToPdf(selCenterClient);
            if (!pdfCenter.IsValid)
                return;

            // Converti selezione client -> "document coordinates" (quello che usa _pageCache)
            var offset = GetScrollOffset();
            var selDoc = new Rectangle(
                sel.Left - offset.Width,
                sel.Top - offset.Height,
                sel.Width,
                sel.Height
            );

            // Trova la pagina (usiamo il centro della selezione in coordinate documento)
            var centerDoc = new Point(selDoc.Left + selDoc.Width / 2, selDoc.Top + selDoc.Height / 2);
            int page = -1;
            for (int p = 0; p < Document.PageCount; p++)
            {
                if (_pageCache[p].OuterBounds.Contains(centerDoc))
                {
                    page = p;
                    break;
                }
            }
            if (page < 0)
                return;

            // Interseca col bounds della pagina (parte bianca)
            var pageBoundsDoc = _pageCache[page].Bounds;
            var selOnPageDoc = Rectangle.Intersect(selDoc, pageBoundsDoc);
            if (selOnPageDoc.Width < 6 || selOnPageDoc.Height < 6)
                return;

            // Calcola zoom target: newZoom = oldZoom * min(viewW/selW, viewH/selH) * margine
            double oldZoom = Zoom;

            double kx = (double)clientArea.Width / selOnPageDoc.Width;
            double ky = (double)clientArea.Height / selOnPageDoc.Height;

            double k = Math.Min(kx, ky) * 0.95; // piccolo margine
            double newZoom = Clamp(oldZoom * k, ZoomMin, ZoomMax);

            // Applica zoom
            Zoom = newZoom;

            // Dopo lo zoom, riconverti il PdfPoint in client e centra la vista su quel punto
            var newClientPoint = PointFromPdf(pdfCenter);
            CenterClientPoint(newClientPoint);
        }

        private void CenterClientPoint(Point clientPoint)
        {
            var clientArea = GetScrollClientArea();
            if (clientArea.Width <= 0 || clientArea.Height <= 0)
                return;

            int desiredX = clientArea.Width / 2;
            int desiredY = clientArea.Height / 2;

            int dx = clientPoint.X - desiredX;
            int dy = clientPoint.Y - desiredY;

            // DisplayRectangle.Location e' lo "scroll offset" del documento: spostiamo la vista dell'opposto del delta
            var display = DisplayRectangle.Location;
            SetDisplayRectLocation(new Point(display.X - dx, display.Y - dy));
        }

        private static Rectangle NormalizeRect(Rectangle r)
        {
            int left = Math.Min(r.Left, r.Right);
            int top = Math.Min(r.Top, r.Bottom);
            int right = Math.Max(r.Left, r.Right);
            int bottom = Math.Max(r.Top, r.Bottom);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static double Clamp(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private Size GetScrollOffset()
        {
            var bounds = GetScrollClientArea();
            int maxWidth = (int)(_maxWidth * _scaleFactor) + ShadeBorder.Size.Horizontal + PageMargin.Horizontal;
            int leftOffset = (HScroll ? DisplayRectangle.X : (bounds.Width - maxWidth) / 2) + maxWidth / 2;
            int topOffset = VScroll ? DisplayRectangle.Y : 0;

            return new Size(leftOffset, topOffset);
        }

        /// <summary>
        /// Raises the <see cref="E:System.Windows.Forms.Control.Layout"/> event.
        /// </summary>
        /// <param name="levent">A <see cref="T:System.Windows.Forms.LayoutEventArgs"/> that contains the event data. </param>
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            UpdateScrollbars();
        }

        /// <summary>
        /// Called when the zoom level changes.
        /// </summary>
        /// <param name="e">The event args.</param>
        protected override void OnZoomChanged(EventArgs e)
        {
            base.OnZoomChanged(e);

            UpdateScrollbars();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch ((e.KeyData) & Keys.KeyCode)
            {
                case Keys.A:
                    if ((e.KeyData & Keys.Modifiers) == Keys.Control)
                        SelectAll();
                    break;

                case Keys.C:
                    if ((e.KeyData & Keys.Modifiers) == Keys.Control)
                        CopySelection();
                    break;
            }

            base.OnKeyDown(e);
        }

        public void SelectAll()
        {
            _textSelectionState = new TextSelectionState()
            {
                StartPage = 0,
                StartIndex = 0,
                EndPage = Document.PageCount - 1,
                EndIndex = Document.CountCharacters(Document.PageCount - 1) - 1
            };

            Invalidate();
        }

        public void SelectCurrentPage()
        {
            _textSelectionState = new TextSelectionState()
            {
                StartPage = Page,
                StartIndex = 0,
                EndPage = Page,
                EndIndex = Document.CountCharacters(Page) - 1
            };

            Invalidate();
        }

        public void CopySelection()
        {
            var text = SelectedText;
            if (text.Length > 0)
                Clipboard.SetText(text);
        }

        /// <summary>
        /// Load a <see cref="IPdfDocument"/> into the control.
        /// </summary>
        /// <param name="document">Document to load.</param>
        public void Load(IPdfDocument document)
        {
            if (document == null)
                throw new ArgumentNullException("document");
            if (document.PageCount == 0)
                throw new ArgumentException("Document does not contain any pages", "document");

            Document = document;

            SetDisplayRectLocation(new Point(0, 0));

            ReloadDocument();

            ResetInteractionToDefault(); //resetta la modalità di interazione
        }

        /// <summary>
        /// Clears the currently loaded <see cref="IPdfDocument"/> from the control.
        /// </summary>
        public void ClearDocument()
        {
            foreach (var pageCache in _pageCache)
            {
                if (pageCache.Image != null)
                {
                    pageCache.Image.Dispose();
                    pageCache.Image = null;
                }
            }

            _pageCache.Clear();
            _pageCacheValid = false;
            _textSelectionState = null;
            _markers = null;
            _visiblePageStart = -1;
            _visiblePageEnd = -1;
            _maxWidth = 0;
            _maxHeight = 0;
            _documentScaleFactor = 0D;
            Document = null;

            SetDisplayRectLocation(new Point(0, 0));
            Invalidate();
        }

        public void ReloadDocument()
        {
            _maxWidth = 0;
            _maxHeight = 0;
            _textSelectionState = null;

            foreach (var size in Document.PageSizes)
            {
                var translated = TranslateSize(size);
                _maxWidth = Math.Max((int)translated.Width, _maxWidth);
                _maxHeight = Math.Max((int)translated.Height, _maxHeight);
            }

            _documentScaleFactor = _maxHeight != 0 ? (double)_maxWidth / _maxHeight : 0D;

            _markers = null;

            UpdateScrollbars();

            Invalidate();
        }

        private void UpdateScrollbars()
        {
            if (Document == null)
                return;

            UpdateScaleFactor(ScrollBars.Both);

            var bounds = GetScrollClientArea(ScrollBars.Both);

            var documentSize = GetDocumentBounds().Size;

            bool horizontalVisible = documentSize.Width > bounds.Width;

            if (!horizontalVisible)
            {
                UpdateScaleFactor(ScrollBars.Vertical);

                documentSize = GetDocumentBounds().Size;
            }

            _suspendPaintCount++;

            try
            {
                SetDisplaySize(documentSize);
            }
            finally
            {
                _suspendPaintCount--;
            }

            RebuildPageCache();
        }

        private void RebuildPageCache()
        {
            if (Document == null || _suspendPaintCount > 0)
                return;

            _pageCacheValid = true;

            int maxWidth = (int)(_maxWidth * _scaleFactor) + ShadeBorder.Size.Horizontal + PageMargin.Horizontal;
            int leftOffset = -maxWidth / 2;

            int offset = 0;

            for (int page = 0; page < Document.PageSizes.Count; page++)
            {
                var size = TranslateSize(Document.PageSizes[page]);
                int height = (int)(size.Height * _scaleFactor);
                int fullHeight = height + ShadeBorder.Size.Vertical + PageMargin.Vertical;
                int width = (int)(size.Width * _scaleFactor);
                int maxFullWidth = (int)(_maxWidth * _scaleFactor) + ShadeBorder.Size.Horizontal + PageMargin.Horizontal;
                int fullWidth = width + ShadeBorder.Size.Horizontal + PageMargin.Horizontal;
                int thisLeftOffset = leftOffset + (maxFullWidth - fullWidth) / 2;

                while (_pageCache.Count <= page)
                {
                    _pageCache.Add(new PageCache());
                }

                var pageCache = _pageCache[page];

                if (pageCache.Image != null)
                {
                    pageCache.Image.Dispose();
                    pageCache.Image = null;
                }

                pageCache.Links = null;
                pageCache.Bounds = new Rectangle(
                    thisLeftOffset + ShadeBorder.Size.Left + PageMargin.Left,
                    offset + ShadeBorder.Size.Top + PageMargin.Top,
                    width,
                    height
                );
                pageCache.OuterBounds = new Rectangle(
                    thisLeftOffset,
                    offset,
                    width + ShadeBorder.Size.Horizontal + PageMargin.Horizontal,
                    height + ShadeBorder.Size.Vertical + PageMargin.Vertical
                );

                offset += fullHeight;
            }

            // Dopo il for principale in RebuildPageCache()
            int newCount = Document.PageSizes.Count;

            for (int i = newCount; i < _pageCache.Count; i++)
            {
                var pc = _pageCache[i];
                if (pc?.Image != null)
                {
                    pc.Image.Dispose();
                    pc.Image = null;
                }
                if (pc != null)
                    pc.Links = null;
            }

            // opzionale: se vuoi proprio accorciare la lista (io lo farei)
            if (_pageCache.Count > newCount)
                _pageCache.RemoveRange(newCount, _pageCache.Count - newCount);

        }

        private static readonly List<PageLink> s_emptyPageLinks = new List<PageLink>(0);

        private List<PageLink> GetPageLinks(int page)
        {
            if (_pageCache == null || page < 0 || page >= _pageCache.Count)
                return s_emptyPageLinks;

            var pageCache = _pageCache[page];
            if (pageCache == null)
                return s_emptyPageLinks;

            // già calcolati (anche vuoti)
            if (pageCache.Links != null)
                return pageCache.Links;

            try
            {
                var doc = Document;
                if (doc == null)
                {
                    pageCache.Links = s_emptyPageLinks;
                    return pageCache.Links;
                }

                var size = pageCache.Bounds.Size;
                if (size.Width <= 0 || size.Height <= 0)
                {
                    pageCache.Links = s_emptyPageLinks;
                    return pageCache.Links;
                }

                var pageLinks = doc.GetPageLinks(page, size);
                var rawLinks = pageLinks?.Links; // IList<PdfPageLink>

                if (rawLinks == null || rawLinks.Count == 0)
                {
                    pageCache.Links = s_emptyPageLinks;
                    return pageCache.Links;
                }

                var list = new List<PageLink>(rawLinks.Count);
                foreach (var link in rawLinks)
                {
                    var clientBounds = BoundsFromPdf(new PdfRectangle(page, link.Bounds), false);
                    list.Add(new PageLink(link, clientBounds));
                }

                pageCache.Links = list;
                return pageCache.Links;
            }
            catch (ObjectDisposedException)
            {
                pageCache.Links = s_emptyPageLinks;
                return pageCache.Links;
            }
            catch (NullReferenceException)
            {
                pageCache.Links = s_emptyPageLinks;
                return pageCache.Links;
            }
            catch
            {
                pageCache.Links = s_emptyPageLinks;
                return pageCache.Links;
            }
        }


        //private List<PageLink> GetPageLinks(int page)
        //{
        //    var pageCache = _pageCache[page];
        //    if (pageCache.Links == null)
        //    {
        //        pageCache.Links = new List<PageLink>();
        //        foreach (var link in Document.GetPageLinks(page, pageCache.Bounds.Size).Links)
        //        {
        //            pageCache.Links.Add(new PageLink(link, BoundsFromPdf(new PdfRectangle(page, link.Bounds), false)));
        //        }
        //    }

        //    return pageCache.Links;
        //}

        private Rectangle GetScrollClientArea()
        {
            ScrollBars scrollBarsVisible;

            if (HScroll && VScroll)
                scrollBarsVisible = ScrollBars.Both;
            else if (HScroll)
                scrollBarsVisible = ScrollBars.Horizontal;
            else if (VScroll)
                scrollBarsVisible = ScrollBars.Vertical;
            else
                scrollBarsVisible = ScrollBars.None;

            return GetScrollClientArea(scrollBarsVisible);
        }

        private Rectangle GetScrollClientArea(ScrollBars scrollbars)
        {
            return new Rectangle(
                0,
                0,
                scrollbars == ScrollBars.Vertical || scrollbars == ScrollBars.Both ? Width - SystemInformation.VerticalScrollBarWidth : Width,
                scrollbars == ScrollBars.Horizontal || scrollbars == ScrollBars.Both ? Height - SystemInformation.HorizontalScrollBarHeight : Height
            );
        }

        private void UpdateScaleFactor(ScrollBars scrollBars)
        {
            var bounds = GetScrollClientArea(scrollBars);

            // Scale factor determines what we need to multiply the dimensions
            // of the metafile with to get the size in the control.

            var zoomMode = CalculateZoomModeForFitBest(bounds);

            if (zoomMode == PdfViewerZoomMode.FitHeight)
            {
                int height = bounds.Height - ShadeBorder.Size.Vertical - PageMargin.Vertical;

                _scaleFactor = ((double)height / _maxHeight) * Zoom;
            }
            else
            {
                int width = bounds.Width - ShadeBorder.Size.Horizontal - PageMargin.Horizontal;

                _scaleFactor = ((double)width / _maxWidth) * Zoom;
            }
        }

        private PdfViewerZoomMode CalculateZoomModeForFitBest(Rectangle bounds)
        {
            if (ZoomMode != PdfViewerZoomMode.FitBest)
            {
                return ZoomMode;
            }

            var controlScaleFactor = (double)bounds.Width / bounds.Height;

            return controlScaleFactor >= _documentScaleFactor ? PdfViewerZoomMode.FitHeight : PdfViewerZoomMode.FitWidth;
        }

        /// <summary>
        /// Raises the <see cref="E:System.Windows.Forms.Control.Paint"/> event.
        /// </summary>
        /// <param name="e">A <see cref="T:System.Windows.Forms.PaintEventArgs"/> that contains the event data. </param>
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Document == null || _suspendPaintCount > 0 || !_pageCacheValid)
                return;

            EnsureMarkers();

            var offset = GetScrollOffset();
            var bounds = GetScrollClientArea();

            using (var brush = new SolidBrush(BackColor))
            {
                e.Graphics.FillRectangle(brush, e.ClipRectangle);
            }

            _visiblePageStart = -1;
            _visiblePageEnd = -1;

            for (int page = 0; page < Document.PageSizes.Count; page++)
            {
                var pageCache = _pageCache[page];
                var rectangle = pageCache.OuterBounds;
                rectangle.Offset(offset.Width, offset.Height);

                if (_visiblePageStart == -1)
                {
                    if (rectangle.Bottom >= 0)
                    {
                        _visiblePageStart = page;
                    }
                    else if (pageCache.Image != null)
                    {
                        pageCache.Image.Dispose();
                        pageCache.Image = null;
                    }
                }

                if (rectangle.Top > bounds.Height)
                {
                    if (_visiblePageEnd == -1)
                        _visiblePageEnd = page - 1;

                    if (pageCache.Image != null)
                    {
                        pageCache.Image.Dispose();
                        pageCache.Image = null;
                    }
                }

                if (e.ClipRectangle.IntersectsWith(rectangle))
                {
                    var pageBounds = pageCache.Bounds;
                    pageBounds.Offset(offset.Width, offset.Height);

                    e.Graphics.FillRectangle(Brushes.White, pageBounds);

                    DrawPageImage(e.Graphics, page, pageBounds);

                    _shadeBorder.Draw(e.Graphics, pageBounds);

                    DrawMarkers(e.Graphics, page);

                    var selectionInfo = _textSelectionState;
                    if (selectionInfo != null)
                        DrawTextSelection(e.Graphics, page, selectionInfo.GetNormalized());
                }
            }

            // Rubberband on top
            if (_mode == PdfViewerInteractionMode.RubberbandZoom && _rbRect.Width > 0 && _rbRect.Height > 0)
            {
                using (var pen = new Pen(Color.Black, 1))
                {
                    pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    e.Graphics.DrawRectangle(pen, _rbRect);
                }
            }


            if (_visiblePageStart == -1)
                _visiblePageStart = 0;
            if (_visiblePageEnd == -1)
                _visiblePageEnd = Document.PageCount - 1;
        }

        private void DrawTextSelection(Graphics graphics, int page, TextSelectionState state)
        {
            if (state.EndPage < 0 || state.EndIndex < 0)
                return;

            if (page >= state.StartPage && page <= state.EndPage)
            {
                int start = 0, end = 0;

                if (page == state.StartPage)
                    start = state.StartIndex;

                if (page == state.EndPage)
                    end = (state.EndIndex + 1);
                else
                    end = Document.CountCharacters(page);

                Region region = null;
                foreach (var rectangle in Document.GetTextRectangles(page, start, end - start))
                {
                    if (region == null)
                        region = new Region(BoundsFromPdf(rectangle));
                    else
                        region.Union(BoundsFromPdf(rectangle));
                }

                if (region != null)
                    graphics.FillRegion(_textSelectionBrush, region);
            }
        }

        private void DrawPageImage(Graphics graphics, int page, Rectangle pageBounds)
        {
            var pageCache = _pageCache[page];

            if (pageCache.Image == null)
                pageCache.Image = Document.Render(page, pageBounds.Width, pageBounds.Height, graphics.DpiX, graphics.DpiY, Rotation, PdfRenderFlags.Annotations);

            graphics.DrawImageUnscaled(pageCache.Image, pageBounds.Location);
        }

        /// <summary>
        /// Gets the document bounds.
        /// </summary>
        /// <returns>The document bounds.</returns>
        protected override Rectangle GetDocumentBounds()
        {
            int scaledHeight = 0;
            for (int page = 0; page < Document.PageSizes.Count; page++)
            {
                var size = TranslateSize(Document.PageSizes[page]);
                scaledHeight += (int)(size.Height * _scaleFactor);
            }

            int height = (int)(scaledHeight + (ShadeBorder.Size.Vertical + PageMargin.Vertical) * Document.PageCount);
            int width = (int)(_maxWidth * _scaleFactor + ShadeBorder.Size.Horizontal + PageMargin.Horizontal);

            var center = new Point(
                DisplayRectangle.Width / 2,
                DisplayRectangle.Height / 2
            );

            if (
                DisplayRectangle.Width > ClientSize.Width ||
                DisplayRectangle.Height > ClientSize.Height
            )
            {
                center.X += DisplayRectangle.Left;
                center.Y += DisplayRectangle.Top;
            }

            return new Rectangle(
                center.X - width / 2,
                center.Y - height / 2,
                width,
                height
            );
        }

        /// <summary>
        /// Called whent he cursor changes.
        /// </summary>
        /// <param name="e">The event args.</param>
        protected override void OnSetCursor(SetCursorEventArgs e)
        {
            // Se stiamo facendo pan col destro
            if (_mode == PdfViewerInteractionMode.RightPan)
            {
                e.Cursor = Cursors.Hand;
                return;
            }

            // Se siamo in text selection, usa la logica originale (IBeam su testo, Hand su link)
            if (_cursorMode == PdfViewerCursorMode.TextSelection)
            {
                _cachedLink = null;

                if (_pageCacheValid)
                {
                    var offset = GetScrollOffset();
                    var location = new Point(e.Location.X - offset.Width, e.Location.Y - offset.Height);

                    for (int page = _visiblePageStart; page <= _visiblePageEnd; page++)
                    {
                        foreach (var link in GetPageLinks(page))
                        {
                            if (link.Bounds.Contains(location))
                            {
                                _cachedLink = link;
                                //e.Cursor = Cursors.Hand;
                                e.Cursor = Cursors.IBeam; //non usiamo Hand sui link in text selection
                                return;
                            }
                        }
                    }

                    var state = GetMouseState(e.Location);
                    if (state.CharacterIndex >= 0)
                    {
                        e.Cursor = Cursors.IBeam;
                        return;
                    }
                }

                // invece di base.OnSetCursor(e);
                e.Cursor = Cursors.IBeam;
                return;
            }

            // Modalità normale: CROCE sempre (tranne sui link se vuoi mantenerli cliccabili)
            _cachedLink = null;
            if (_pageCacheValid)
            {
                var offset = GetScrollOffset();
                var location = new Point(e.Location.X - offset.Width, e.Location.Y - offset.Height);

                for (int page = _visiblePageStart; page <= _visiblePageEnd; page++)
                {
                    foreach (var link in GetPageLinks(page))
                    {
                        if (link.Bounds.Contains(location))
                        {
                            _cachedLink = link;
                            //e.Cursor = Cursors.Hand; // opzionale: commenta se vuoi croce anche sui link
                            e.Cursor = Cursors.Cross; // croce anche sui link
                            return;
                        }
                    }
                }
            }

            e.Cursor = Cursors.Cross;
        }

        //protected override void OnSetCursorOld(SetCursorEventArgs e)
        //{
        //    _cachedLink = null;

        //    if (_pageCacheValid)
        //    {
        //        var offset = GetScrollOffset();

        //        var location = new Point(
        //            e.Location.X - offset.Width,
        //            e.Location.Y - offset.Height
        //        );

        //        for (int page = _visiblePageStart; page <= _visiblePageEnd; page++)
        //        {
        //            foreach (var link in GetPageLinks(page))
        //            {
        //                if (link.Bounds.Contains(location))
        //                {
        //                    _cachedLink = link;
        //                    e.Cursor = Cursors.Hand;
        //                    return;
        //                }
        //            }
        //        }

        //        if (_cursorMode == PdfViewerCursorMode.TextSelection)
        //        {
        //            var state = GetMouseState(e.Location);
        //            if (state.CharacterIndex >= 0)
        //            {
        //                e.Cursor = Cursors.IBeam;
        //                return;
        //            }
        //        }
        //    }

        //    base.OnSetCursor(e);
        //}

        /// <summary>Raises the <see cref="E:System.Windows.Forms.Control.MouseDown" /> event.</summary>
        /// <param name="e">A <see cref="T:System.Windows.Forms.MouseEventArgs" /> that contains the event data. </param>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            // RIGHT o MIDDLE: esci SEMPRE dalla TextSelection e torna alla croce
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                if (_cursorMode == PdfViewerCursorMode.TextSelection)
                {
                    // chiudi eventuale selezione testo in corso
                    _isSelectingText = false;
                    Capture = false;

                    // torna alla modalità normale (rubberband + croce)
                    CursorMode = PdfViewerCursorMode.Pan;   // "Pan" qui lo usiamo come modalità base del viewer
                    Cursor = Cursors.Cross;
                    Invalidate();
                }
            }
            if (_mode == PdfViewerInteractionMode.RubberbandZoom && e.Button == MouseButtons.Right)
            {
                // cancella rubberband
                _mode = PdfViewerInteractionMode.None;
                _rbRect = Rectangle.Empty;
                _rbPrevRect = Rectangle.Empty;
                Capture = false;
                Invalidate();
            }

            // MIDDLE: reset FitBest
            if (e.Button == MouseButtons.Middle)
            {
                // Reset "fit"
                ZoomMode = PdfViewerZoomMode.FitBest;
                Zoom = 1.0;

                // cursore torna croce
                Cursor = Cursors.Cross;
                return; // non chiamare base
            }

            // RIGHT: pan temporaneo
            if (e.Button == MouseButtons.Right && _cursorMode != PdfViewerCursorMode.TextSelection)
            {
                // pan solo se c'e' qualcosa da pannare
                bool canPan = DisplayRectangle.Width > ClientSize.Width || DisplayRectangle.Height > ClientSize.Height;
                if (canPan)
                {
                    _mode = PdfViewerInteractionMode.RightPan;
                    _panStart = e.Location;
                    _panDisplayStart = DisplayRectangle.Location;
                    Capture = true;
                    Cursor = Cursors.Hand;
                    return; // non chiamare base
                }
            }

            // LEFT: rubberband zoom (solo se NON siamo in text selection)
            if (e.Button == MouseButtons.Left && _cursorMode != PdfViewerCursorMode.TextSelection)
            {
                _mode = PdfViewerInteractionMode.RubberbandZoom;
                _rbStart = e.Location;
                _rbRect = Rectangle.Empty;
                _rbPrevRect = Rectangle.Empty;

                Capture = true;
                Cursor = Cursors.Cross;
                return; // non chiamare base (evita pan automatico)
            }

            // Text selection e link: usa comportamento originale
            base.OnMouseDown(e);

            //HandleMouseDownForLinks(e);
            if (_cursorMode == PdfViewerCursorMode.TextSelection)
                HandleMouseDownForTextSelection(e);
        }
        //protected override void OnMouseDownOld(MouseEventArgs e)
        //{
        //    base.OnMouseDown(e);

        //    HandleMouseDownForLinks(e);

        //    if (_cursorMode == PdfViewerCursorMode.TextSelection)
        //    {
        //        HandleMouseDownForTextSelection(e);
        //    }
        //}

        /// <summary>Raises the <see cref="E:System.Windows.Forms.Control.MouseUp" /> event.</summary>
        /// <param name="e">A <see cref="T:System.Windows.Forms.MouseEventArgs" /> that contains the event data. </param>
        protected override void OnMouseUp(MouseEventArgs e)
        {
            // finish right pan
            if (_mode == PdfViewerInteractionMode.RightPan && e.Button == MouseButtons.Right)
            {
                Capture = false;
                _mode = PdfViewerInteractionMode.None;

                Cursor = Cursors.Cross; // torna croce come richiesto
                base.OnMouseUp(e);
                return;
            }

            // finish rubberband -> zoom
            if (_mode == PdfViewerInteractionMode.RubberbandZoom && e.Button == MouseButtons.Left)
            {
                Capture = false;
                var rect = _rbRect;

                _mode = PdfViewerInteractionMode.None;
                _rbRect = Rectangle.Empty;
                _rbPrevRect = Rectangle.Empty;

                Cursor = Cursors.Cross; // torna croce come richiesto
                Invalidate();

                if (rect.Width >= 8 && rect.Height >= 8)
                    ZoomToClientRectangle(rect);

                base.OnMouseUp(e);
                return;
            }

            // middle up: torna croce
            if (e.Button == MouseButtons.Middle)
            {
                Cursor = Cursors.Cross;
                base.OnMouseUp(e);
                return;
            }

            base.OnMouseUp(e);

            //HandleMouseUpForLinks(e);
            if (_cursorMode == PdfViewerCursorMode.TextSelection)
                HandleMouseUpForTextSelection(e);
        }


        //protected override void OnMouseUp(MouseEventArgs e)
        //{
        //    base.OnMouseUp(e);

        //    HandleMouseUpForLinks(e);

        //    if (_cursorMode == PdfViewerCursorMode.TextSelection)
        //    {
        //        HandleMouseUpForTextSelection(e);
        //    }
        //}

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // Right pan
            if (_mode == PdfViewerInteractionMode.RightPan && Capture)
            {
                var dx = e.Location.X - _panStart.X;
                var dy = e.Location.Y - _panStart.Y;

                SetDisplayRectLocation(new Point(_panDisplayStart.X + dx, _panDisplayStart.Y + dy));
                return;
            }

            // Rubberband
            if (_mode == PdfViewerInteractionMode.RubberbandZoom && Capture)
            {
                var newRect = NormalizeRect(Rectangle.FromLTRB(_rbStart.X, _rbStart.Y, e.X, e.Y));
                var invalidate = Rectangle.Union(_rbPrevRect, newRect);
                invalidate.Inflate(3, 3);

                _rbPrevRect = newRect;
                _rbRect = newRect;

                //Invalidate(invalidate);
                Invalidate(); // più semplice e non si perde mai
                return;
            }

            base.OnMouseMove(e);

            if (_cursorMode == PdfViewerCursorMode.TextSelection)
                HandleMouseMoveForTextSelection(e);
        }

        //protected override void OnMouseMove(MouseEventArgs e)
        //{
        //    base.OnMouseMove(e);

        //    if (_cursorMode == PdfViewerCursorMode.TextSelection)
        //    {
        //        HandleMouseMoveForTextSelection(e);
        //    }
        //}


        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            // DOUBLE CLICK DESTRO => reset FitBest + torna in modalità "normale" (croce + zoom-rect)
            if (e.Button == MouseButtons.Right)
            {
                ResetViewToFitBest(keepCurrentPage: true);
                return;
            }


            if (e.Button == MouseButtons.Left)
            {
                // Proviamo a capire subito se sotto il mouse c'è una parola
                var pdfLocation = PointToPdf(e.Location);
                bool hasWord = false;

                if (pdfLocation.IsValid && Document != null)
                {
                    hasWord = Document.GetWordAtPosition(pdfLocation, 4f, 4f, out var _);
                }

                // Se NON c'è testo -> torna subito in modalità ZoomRect + croce
                if (!hasWord)
                {
                    _textSelectionState = null;
                    _isSelectingText = false;
                    Capture = false;

                    CursorMode = PdfViewerCursorMode.Pan;   // nel tuo fork = modalità "normale" (croce + rubberband)
                    _mode = PdfViewerInteractionMode.None;

                    Cursor = Cursors.Cross;
                    Invalidate();
                    return;
                }

                // Se c'è testo: entra/resta in TextSelection, seleziona parola e copia
                if (_cursorMode != PdfViewerCursorMode.TextSelection)
                    CursorMode = PdfViewerCursorMode.TextSelection;

                HandleMouseDoubleClickForTextSelection(e);

                if (AutoCopyTextSelectionToClipboard)
                    TryCopySelectionToClipboard();

                return;
            }

            base.OnMouseDoubleClick(e);
        }



        //protected override void OnMouseDoubleClick(MouseEventArgs e)
        //{
        //    base.OnMouseDoubleClick(e);

        //    if (_cursorMode == PdfViewerCursorMode.TextSelection)
        //    {
        //        HandleMouseDoubleClickForTextSelection(e);
        //    }
        //}

        private void HandleMouseDownForLinks(MouseEventArgs e)
        {
            _dragState = null;

            if (_cachedLink != null)
            {
                _dragState = new DragState
                {
                    Link = _cachedLink.Link,
                    Location = e.Location
                };
            }
        }

        private void HandleMouseUpForLinks(MouseEventArgs e)
        {
            if (_dragState == null)
                return;

            int dx = Math.Abs(e.Location.X - _dragState.Location.X);
            int dy = Math.Abs(e.Location.Y - _dragState.Location.Y);

            var link = _dragState.Link;
            _dragState = null;

            if (link == null)
                return;

            if (dx <= SystemInformation.DragSize.Width && dy <= SystemInformation.DragSize.Height)
            {
                var linkClickEventArgs = new LinkClickEventArgs(link);
                HandleLinkClick(linkClickEventArgs);
            }
        }

        private void HandleLinkClick(LinkClickEventArgs e)
        {
            OnLinkClick(e);

            if (e.Handled)
                return;

            if (e.Link.TargetPage.HasValue)
                Page = e.Link.TargetPage.Value;

            if (e.Link.Uri != null)
            {
                try
                {
                    Process.Start(e.Link.Uri);
                }
                catch
                {
                    // Some browsers (Firefox) will cause an exception to
                    // be thrown (when it auto-updates).
                }
            }
        }

        private void HandleMouseDownForTextSelection(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            var pdfLocation = PointToPdf(e.Location);
            if (!pdfLocation.IsValid)
                return;

            var characterIndex = Document.GetCharacterIndexAtPosition(pdfLocation, 4f, 4f);

            if (characterIndex >= 0)
            {
                _textSelectionState = new TextSelectionState()
                {
                    StartPage = pdfLocation.Page,
                    StartIndex = characterIndex,
                    EndPage = -1,
                    EndIndex = -1
                };
                _isSelectingText = true;
                Capture = true;
            }
            else
            {
                _isSelectingText = false;
                Capture = false;
                _textSelectionState = null;
            }
        }

        private void HandleMouseUpForTextSelection(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            _isSelectingText = false;
            Capture = false;
            Invalidate();

            // Copia automaticamente alla fine del drag
            if (AutoCopyTextSelectionToClipboard)
                TryCopySelectionToClipboard();
        }

        //private void HandleMouseUpForTextSelection(MouseEventArgs e)
        //{
        //    if (e.Button != MouseButtons.Left)
        //        return;

        //    _isSelectingText = false;
        //    Capture = false;
        //    Invalidate();
        //}

        private void HandleMouseMoveForTextSelection(MouseEventArgs e)
        {
            if (!_isSelectingText)
                return;

            var mouseState = GetMouseState(e.Location);

            if (mouseState.CharacterIndex >= 0)
            {
                _textSelectionState.EndPage = mouseState.PdfLocation.Page;
                _textSelectionState.EndIndex = mouseState.CharacterIndex;

                Invalidate();
            }
        }

        private void HandleMouseDoubleClickForTextSelection(MouseEventArgs e)
        {
            var pdfLocation = PointToPdf(e.Location);
            if (!pdfLocation.IsValid)
                return;

            if (Document.GetWordAtPosition(pdfLocation, 4f, 4f, out var word))
            {
                _textSelectionState = new TextSelectionState()
                {
                    StartPage = pdfLocation.Page,
                    EndPage = pdfLocation.Page,
                    StartIndex = word.Offset,
                    EndIndex = word.Offset + word.Length
                };

                Invalidate();
            }
        }
        private MouseState GetMouseState(Point mouseLocation)
        {
            var currentState = _cachedMouseState;
            if (currentState?.MouseLocation == mouseLocation)
                return currentState;

            _cachedMouseState = new MouseState()
            {
                MouseLocation = mouseLocation
            };

            try
            {
                if (Document == null)
                    return _cachedMouseState;

                _cachedMouseState.PdfLocation = PointToPdf(mouseLocation);

                if (!_cachedMouseState.PdfLocation.IsValid)
                    return _cachedMouseState;

                _cachedMouseState.CharacterIndex =
                    Document.GetCharacterIndexAtPosition(_cachedMouseState.PdfLocation, 4f, 4f);
            }
            catch
            {
                // non deve mai crashare il cursor/mouse move
                _cachedMouseState.PdfLocation = PdfPoint.Empty;
                _cachedMouseState.CharacterIndex = -1;
            }

            return _cachedMouseState;
        }

        //private MouseState GetMouseState(Point mouseLocation)
        //{
        //    // OnMouseMove and OnSetCursor get invoked a lot, often multiple times in succession for the same point.
        //    // By just caching the mouse state for the last known position we can save a lot of work.

        //    var currentState = _cachedMouseState;
        //    if (currentState?.MouseLocation == mouseLocation)
        //        return currentState;

        //    _cachedMouseState = new MouseState()
        //    {
        //        MouseLocation = mouseLocation,
        //        PdfLocation = PointToPdf(mouseLocation)
        //    };

        //    if (!_cachedMouseState.PdfLocation.IsValid)
        //        return _cachedMouseState;

        //    _cachedMouseState.CharacterIndex = Document.GetCharacterIndexAtPosition(_cachedMouseState.PdfLocation, 4f, 4f);

        //    return _cachedMouseState;
        //}


        private void TryCopySelectionToClipboard()
        {
            try
            {
                if (!IsTextSelected)
                    return;

                var text = SelectedText;
                if (string.IsNullOrWhiteSpace(text))
                    return;

                Clipboard.SetText(text);
            }
            catch
            {
                // In alcuni contesti (remote session / clipboard busy) può fallire.
                // Evitiamo eccezioni.
            }
        }


        /// <summary>
        /// Occurs when a link in the pdf document is clicked.
        /// </summary>
        [Category("Action")]
        [Description("Occurs when a link in the pdf document is clicked.")]
        public event LinkClickEventHandler LinkClick;

        /// <summary>
        /// Called when a link is clicked.
        /// </summary>
        /// <param name="e">The event args.</param>
        protected virtual void OnLinkClick(LinkClickEventArgs e)
        {
            var handler = LinkClick;
            if (handler != null)
                handler(this, e);
        }

        /// <summary>
        /// Rotate the PDF document left.
        /// </summary>
        public void RotateLeft()
        {
            Rotation = (PdfRotation)(((int)Rotation + 3) % 4);
        }

        /// <summary>
        /// Rotate the PDF document right.
        /// </summary>
        public void RotateRight()
        {
            Rotation = (PdfRotation)(((int)Rotation + 1) % 4);
        }

        private void ResetFromRotation()
        {
            var offsetX = (double)DisplayRectangle.Left / DisplayRectangle.Width;
            var offsetY = (double)DisplayRectangle.Top / DisplayRectangle.Height;

            ReloadDocument();

            SetDisplayRectLocation(new Point(
                (int)(DisplayRectangle.Width * offsetX),
                (int)(DisplayRectangle.Height * offsetY)
            ));
        }

        private SizeF TranslateSize(SizeF size)
        {
            switch (Rotation)
            {
                case PdfRotation.Rotate90:
                case PdfRotation.Rotate270:
                    return new SizeF(size.Height, size.Width);

                default:
                    return size;
            }
        }

        /// <summary>
        /// Called when the zoom level changes.
        /// </summary>
        /// <param name="zoom">The new zoom level.</param>
        /// <param name="focus">The location to focus on.</param>
        protected override void SetZoom(double zoom, Point? focus)
        {
            base.SetZoom(zoom, focus);
        }

        //protected override void SetZoom(double zoom, Point? focus)
        //{
        //    base.SetZoom(zoom, null);
        //}

        private void RedrawMarkers()
        {
            _markers = null;

            Invalidate();
        }

        private void EnsureMarkers()
        {
            if (_markers != null)
                return;

            _markers = new List<IPdfMarker>[_pageCache.Count];

            foreach (var marker in Markers)
            {
                if (marker.Page < 0 || marker.Page >= _markers.Length)
                    continue;

                if (_markers[marker.Page] == null)
                    _markers[marker.Page] = new List<IPdfMarker>();

                _markers[marker.Page].Add(marker);
            }
        }

        private void DrawMarkers(Graphics graphics, int page)
        {
            var markers = _markers[page];
            if (markers == null)
                return;

            foreach (var marker in markers)
            {
                marker.Draw(this, graphics);
            }
        }

        /// <summary>
        /// Scroll the PDF bounds into view.
        /// </summary>
        /// <param name="bounds">The PDF bounds to scroll into view.</param>
        public void ScrollIntoView(PdfRectangle bounds)
        {
            ScrollIntoView(BoundsFromPdf(bounds));
        }

        /// <summary>
        /// Scroll the client rectangle into view.
        /// </summary>
        /// <param name="rectangle">The client rectangle to scroll into view.</param>
        public void ScrollIntoView(Rectangle rectangle)
        {
            var clientArea = GetScrollClientArea();

            if (rectangle.Top < 0 || rectangle.Bottom > clientArea.Height)
            {
                var displayRectangle = DisplayRectangle;
                int center = rectangle.Top + rectangle.Height / 2;
                int documentCenter = center - displayRectangle.Y;
                int displayCenter = clientArea.Height / 2;
                int offset = documentCenter - displayCenter;

                SetDisplayRectLocation(new Point(
                    displayRectangle.X,
                    -offset
                ));
            }
        }

        private void ResetViewToFitBest(bool keepCurrentPage = true)
        {
            if (Document == null)
                return;

            int page = 0;
            if (keepCurrentPage && _pageCacheValid)
                page = Page;

            // reset zoom "fit"
            ZoomMode = PdfViewerZoomMode.FitBest;
            Zoom = 1.0;

            // forza layout/cache aggiornati (FitBest dipende dalla size del controllo)
            PerformLayout();

            // resta sulla stessa pagina (se possibile)
            if (keepCurrentPage && Document != null)
            {
                try { Page = page; } catch { /* ignore */ }
            }
            else
            {
                SetDisplayRectLocation(new Point(0, 0));
            }

            // torna alla modalità base: croce + rubberband zoom (nel tuo fork)
            ResetInteractionToDefault();
        }

        public void ResetInteractionToDefault()
        {
            // chiudi rubberband/pan
            _mode = PdfViewerInteractionMode.None;
            _rbRect = Rectangle.Empty;
            _rbPrevRect = Rectangle.Empty;

            // chiudi selezione testo
            _textSelectionState = null;
            _isSelectingText = false;
            Capture = false;

            // torna alla modalità base
            CursorMode = PdfViewerCursorMode.Pan;
            Cursor = Cursors.Cross;
            Invalidate();
        }


        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="T:System.Windows.Forms.Control"/> and its child controls and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources. </param>
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                if (_shadeBorder != null)
                {
                    _shadeBorder.Dispose();
                    _shadeBorder = null;
                }

                if (_toolTip != null)
                {
                    _toolTip.Dispose();
                    _toolTip = null;
                }

                foreach (var pageCache in _pageCache)
                {
                    if (pageCache.Image != null)
                    {
                        pageCache.Image.Dispose();
                        pageCache.Image = null;
                    }
                }

                _disposed = true;
            }

            base.Dispose(disposing);
        }

        private class PageCache
        {
            public List<PageLink> Links { get; set; }
            public Rectangle Bounds { get; set; }
            public Rectangle OuterBounds { get; set; }
            public Image Image { get; set; }
        }

        private class PageLink
        {
            public PdfPageLink Link { get; }
            public Rectangle Bounds { get; }

            public PageLink(PdfPageLink link, Rectangle bounds)
            {
                Link = link;
                Bounds = bounds;
            }
        }

        private class DragState
        {
            public PdfPageLink Link { get; set; }
            public Point Location { get; set; }
        }

        private class MouseState
        {
            public Point MouseLocation { get; set; }
            public PdfPoint PdfLocation { get; set; }
            public int CharacterIndex { get; set; }
        }

        private class TextSelectionState
        {
            public int StartPage { get; set; }
            public int StartIndex { get; set; }
            public int EndPage { get; set; }
            public int EndIndex { get; set; }

            public TextSelectionState GetNormalized()
            {
                if (EndPage < 0 || EndIndex < 0) // Special case when only start position is known
                    return this;

                if (EndPage < StartPage ||
                    (StartPage == EndPage && EndIndex < StartIndex))
                {
                    // End position is before start position.
                    // Swap positions so start is always before end.

                    return new TextSelectionState()
                    {
                        StartPage = EndPage,
                        StartIndex = EndIndex,
                        EndPage = StartPage,
                        EndIndex = StartIndex
                    };
                }

                return this;
            }
        }
    }
}
