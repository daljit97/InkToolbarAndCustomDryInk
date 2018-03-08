using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Input;
using Windows.Devices.Input;
using Windows.UI.Input;
using Windows.UI.Xaml.Shapes;
using CompositionTarget = Windows.UI.Xaml.Media.CompositionTarget;

namespace InkToolbarTest
{
    /// <summary>
    ///     Ink Canvas, Drawing Canvas, and Canvas Control
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private enum ToolbarMode
        {
            Drawing,
            Erasing,
            Lasso
        }


        /// <summary>
        /// This is the maximum Bitmap render size for Win2D
        /// </summary>
        const int MaxImageSize = 16384;

        #region Fields
        private readonly List<InkStrokeContainer> strokes = new List<InkStrokeContainer>();
        private Flyout eraseAllFlyout;
        private InkSynchronizer inkSynchronizer;
        private float displayDpi;
        private ToolbarMode toolbarMode;
        #endregion

        #region multi stylus support
        // please  verify if for a non async method lock statement works on uwp as on desktop or not
        //private AsyncLock _locker = new AsyncLock();

        //used to store InkDraingAttribute of esch surface hub stylus
        private readonly Dictionary<int, InkDrawingAttributes> stylusAttributes = new Dictionary<int, InkDrawingAttributes>();

        private IReadOnlyList<InkStroke> pendingDry;
        private readonly List<InkStroke> renderedStrokes = new List<InkStroke>();
        private int deferredDryDelay;
        private Point lastPoint;
        private bool isErasing;
        private Rect? selectionRectangle;
        private Point selectionDragPoint;
        private Size selectionOffset;
        private bool isSelectionDragging;
        private bool isClickedOutsizeSelection;
        private Polyline lasso;
        private bool isBoundRect;
        private bool hasPasted;
        private InkStrokeContainer copyContainer;
        private int activePointerId;
        private Point toolbarPosition;

        #endregion

        #region Constructors
        public MainPage()
        {
            InitializeComponent();

            Loaded += MainPage_Loaded;
        }
        #endregion

        #region Methods

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Enable sharing
            DataTransferManager.GetForCurrentView().DataRequested += MainPage_DataRequested;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Disable sharing
            DataTransferManager.GetForCurrentView().DataRequested -= MainPage_DataRequested;
        }
        #endregion

        #region Implementation

        private async void MainPage_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {

        }


        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            var display = DisplayInformation.GetForCurrentView();

            display.DpiChanged += Display_DpiChanged;

            Display_DpiChanged(display, null);

            var maxSize = Math.Max(CanvasContainer.Width, CanvasContainer.Height);
            ScrollViewer.MaxZoomFactor = MaxImageSize / System.Convert.ToSingle(maxSize);


            #region multi stylus support
            // Set supported inking device types.
            InkCanvas.InkPresenter.InputDeviceTypes = Windows.UI.Core.CoreInputDeviceTypes.Pen | Windows.UI.Core.CoreInputDeviceTypes.Mouse;

            // Phase 1: we need to discover the three user clicks with pen on the toolbar, we store only the unique stylus id (into Dictionary<int, InkDrawingAttributes> _stylusAttributes) and no InkDrawingAttributes because at this time user has still 
            // not choosen the tool and color. So in that phase we do have an entry on the _stylusAttributes with a key but with a null value

            // get reference to inktoolbar buttons
            InkToolbarBallpointPenButton penButton = InkToolbar.GetToolButton(InkToolbarTool.BallpointPen) as InkToolbarBallpointPenButton;
            InkToolbarHighlighterButton highlighterButton = InkToolbar.GetToolButton(InkToolbarTool.Highlighter) as InkToolbarHighlighterButton;
            InkToolbarPencilButton pencilButton = InkToolbar.GetToolButton(InkToolbarTool.Pencil) as InkToolbarPencilButton;

            // subscribing to inktoolbar button events
            // TODO: unsubscribe to all those events
            if (penButton != null)
            {
                penButton.PointerEntered += this.OnConfigButtonPointerEntered;
                penButton.Click += this.OnPenButtonClicked;
            }

            // TODO: unsubscribe to all those events
            if (highlighterButton != null)
            {
                highlighterButton.PointerEntered += this.OnConfigButtonPointerEntered;
                highlighterButton.Click += this.OnPenButtonClicked;
            }

            // TODO: unsubscribe to all those events
            if (pencilButton != null)
            {
                pencilButton.PointerEntered += this.OnConfigButtonPointerEntered;
                pencilButton.Click += this.OnPenButtonClicked;
            }

            this.InkToolbar.ActiveToolChanged += this.OnActiveToolbarToolChanged;
            this.InkToolbar.InkDrawingAttributesChanged += this.OnToolbarAttributesChanged;

            // Phase 1 (ConfigControl_PointerExited): Every time user select (or not) a new property we sotre it for its own unique stylus id
            // Phase 2 (unprocessedInput.PointerHovered): when the user starts inking just a moment before the ink starts we get the unique stylus id from the PointerHovered
            // we look into _stylusAttributes for the InkDrawingAttributes of that stylus and we apply to the InkCanvas.InkPresenter.UpdateDefaultDrawingAttributes
            #endregion


            // 1. Activate custom drawing 
            this.inkSynchronizer = InkCanvas.InkPresenter.ActivateCustomDrying();

            InkCanvas.InkPresenter.SetPredefinedConfiguration(InkPresenterPredefinedConfiguration.SimpleMultiplePointer);


            // 2. add use custom drawing when strokes are collected
            InkCanvas.InkPresenter.StrokesCollected += InkPresenter_StrokesCollected;


            // 3. Get the eraser button to handle custom dry ink and replace the erase all button with new logic
            var eraser = InkToolbar.GetToolButton(InkToolbarTool.Eraser) as InkToolbarEraserButton;

            if (eraser != null)
            {
                eraser.Checked += Eraser_Checked;
                eraser.Unchecked += Eraser_Unchecked;
            }

            InkCanvas.InkPresenter.InputProcessingConfiguration.RightDragAction = InkInputRightDragAction.LeaveUnprocessed;

            var unprocessedInput = InkCanvas.InkPresenter.UnprocessedInput;
            unprocessedInput.PointerPressed += UnprocessedInput_PointerPressed;
            unprocessedInput.PointerMoved += UnprocessedInput_PointerMoved;
            unprocessedInput.PointerReleased += UnprocessedInput_PointerReleased;
            unprocessedInput.PointerExited += UnprocessedInput_PointerExited;
            unprocessedInput.PointerLost += UnprocessedInput_PointerLost;
            unprocessedInput.PointerHovered += UnprocessedInput_PointerHovered;

            this.eraseAllFlyout = FlyoutBase.GetAttachedFlyout(eraser) as Flyout;

            if (this.eraseAllFlyout != null)
            {
                var button = this.eraseAllFlyout.Content as Button;

                if (button != null)
                {
                    var newButton = new Button();
                    newButton.Style = button.Style;
                    newButton.Content = button.Content;

                    newButton.Click += EraseAllInk;
                    this.eraseAllFlyout.Content = newButton;
                }
            }

            this.InkCanvas.Holding += this.OnHolding;
        }

        private void OnHolding(object sender, HoldingRoutedEventArgs e)
        {
            base.OnHolding(e);
            if (e.HoldingState == HoldingState.Started)
            {
                this.ShowInkSelectionToolbar();
            }
        }

        private void ShowInkSelectionToolbar()
        {
            bool isSelectionActive = this.selectionRectangle != null;
            bool canPaste = this.copyContainer != null && this.copyContainer.CanPasteFromClipboard();
            if (isSelectionActive || canPaste)
            {
                this.InkSelectionToolbar.Visibility = Visibility.Visible;
            }
        }

        #region multi stylus support

        // Phase 1:
        // Every time user select (or not) a new property we sotre it for its own unique stylus id
        private void OnConfigButtonPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            this.activePointerId = this.GetUniqueStylusId(PointerPoint.GetCurrentPoint(e.Pointer.PointerId));
        }

        // Phase 2: 
        // this method need to be super veloce because this happens, we have the chance to change the color but the inking will start even this isn't finished
        // so it can happens that if two stylust start exactly at the same time inking one of the two whon't have it's own DrawingAttributes stored in _stylusAttributes
        // because the ininkg process has started and we din't changed the DrawingAttributes 
        private void UnprocessedInput_PointerHovered(InkUnprocessedInput sender, PointerEventArgs e)
        {
            int uniqueStylusId = this.GetUniqueStylusId(e.CurrentPoint);
            if (this.stylusAttributes.ContainsKey(uniqueStylusId) && this.stylusAttributes[uniqueStylusId] != null)
            {
                this.InkCanvas.InkPresenter.UpdateDefaultDrawingAttributes(this.stylusAttributes[uniqueStylusId]);
            }
            else
            {
                this.SetStylusAttributes(uniqueStylusId);
            }

        }

        private void SetStylusAttributes(int uniqueStylusId)
        {
            if (this.InkCanvas != null)
            {
                if (this.stylusAttributes.ContainsKey(uniqueStylusId))
                {
                    this.stylusAttributes[uniqueStylusId] = this.InkCanvas.InkPresenter.CopyDefaultDrawingAttributes();
                }
                else
                {
                    this.stylusAttributes.Add(uniqueStylusId, this.InkCanvas.InkPresenter.CopyDefaultDrawingAttributes());
                }
            }
        }

        private void OnToolbarAttributesChanged(InkToolbar sender, object args)
        {
            this.SetStylusAttributes(this.activePointerId);
        }

        private void OnActiveToolbarToolChanged(InkToolbar sender, object args)
        {
            this.SetStylusAttributes(this.activePointerId);
        }

        private const int UNIQUE_STYLUS_ID_NOT_PRESENT = -1;
        private const uint WIRELESS_ID_USAGE_PAGE = 0x0D;
        private const uint WIRELESS_ID_USAGE = 0x5B;
        private int GetUniqueStylusId(PointerPoint point)
        {
            int retVal = UNIQUE_STYLUS_ID_NOT_PRESENT;

            try
            {
                if (point.Properties.HasUsage(WIRELESS_ID_USAGE_PAGE, WIRELESS_ID_USAGE))
                    retVal = point.Properties.GetUsageValue(WIRELESS_ID_USAGE_PAGE, WIRELESS_ID_USAGE);
            }
            catch (Exception ex)
            {
                //Todo log exception
                retVal = UNIQUE_STYLUS_ID_NOT_PRESENT;
            }
            return retVal;
        }
        private void OnPenButtonClicked(object sender, RoutedEventArgs e)
        {
            this.ClearDrawnBoundingRect();
            this.LassoButton.IsChecked = false;
        }
        #endregion

        /// <summary>
        /// Update the Scroll Viewer when the DPI changes
        /// </summary>
        /// <param name="sender">the display information</param>
        /// <param name="args">the arguments</param>
        /// <remarks>Adapted from Win2D Gallery Mandelbrot sample at
        /// <![CDATA[https://github.com/Microsoft/Win2D/blob/master/samples/ExampleGallery/Shared/Mandelbrot.xaml.cs]]>
        /// </remarks>
        private void Display_DpiChanged(DisplayInformation sender, object args)
        {
            displayDpi = sender.LogicalDpi;

            OnScrollViewerViewChanged(null, null);
        }

        #region Erase

        private void EraseAllInk(object sender, RoutedEventArgs e)
        {
            this.strokes.Clear();
            this.canvas.Invalidate();

            this.eraseAllFlyout.Hide();
        }

        private void Eraser_Checked(object sender, RoutedEventArgs e)
        {
            this.toolbarMode = ToolbarMode.Erasing;
            this.InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.None;

        }

        private void Eraser_Unchecked(object sender, RoutedEventArgs e)
        {
            this.toolbarMode = this.toolbarMode = ToolbarMode.Drawing;
            this.InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
        }

        private void OnEraserClicked(object sender, RoutedEventArgs e)
        {

            if (this.SelectionCanvas.Children.Any())
            {
                bool needRedraw = false;

                List<InkStrokeContainer> tempStrokes = new List<InkStrokeContainer>();
                foreach (InkStrokeContainer item in this.strokes.ToArray())
                {
                    item.DeleteSelected();
                    if (item.GetStrokes().Any())
                    {
                        tempStrokes.Add(item);
                    }

                    needRedraw = true;
                }

                this.strokes.Clear();
                this.strokes.AddRange(tempStrokes);
                tempStrokes = null;

                if (needRedraw)
                {
                    this.canvas.Invalidate();
                    this.ClearDrawnBoundingRect();
                }
            }
        }
        #endregion

        #region Lasso + copy/paste/delete

        private void OnLassoChecked(object sender, RoutedEventArgs e)
        {
            if (!this.hasPasted)
            {
                this.ClearDrawnBoundingRect();
            }


            this.InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.None;
            this.toolbarMode = ToolbarMode.Lasso;
        }

        private void OnLassoUnchecked(object sender, RoutedEventArgs e)
        {
            this.toolbarMode = ToolbarMode.Drawing;
        }

        private bool Intersects(Rect rect1, Rect rect2)
        {
            Rect clone = new Rect(new Point(rect1.X, rect1.Y), new Size(rect1.Width, rect1.Height));
            clone.Intersect(rect2);
            return clone != Rect.Empty;
        }

        object _eraserLock = new { };
        Rect eraser = new Rect(new Point(0, 0), new Size(0, 0));
        private void UnprocessedInput_PointerMoved(InkUnprocessedInput sender, PointerEventArgs args)
        {
            switch (this.toolbarMode)
            {
                case ToolbarMode.Erasing:
                    if (!this.isErasing) return;
                    lock (_eraserLock)
                    {
                        eraser = new Rect(args.CurrentPoint.Position, new Size(30, 30));

                        var tempStrokes = new List<InkStrokeContainer>(strokes);
                        foreach (var strokeContainer in tempStrokes)
                        {
                            if (Intersects(eraser, strokeContainer.BoundingRect))
                            {
                                var innerStrokes = new List<InkStroke>(strokeContainer.GetStrokes());
                                for (var i = 0; i < innerStrokes.Count; i += 1)
                                {
                                    var stroke = innerStrokes[i];

                                    if (Intersects(eraser, stroke.BoundingRect))
                                    {
                                        var inkPoints = new List<InkPoint>(stroke.GetInkPoints());

                                        for (int pointIndex = 0; pointIndex < inkPoints.Count; pointIndex++)
                                        {
                                            var inkPoint = inkPoints[pointIndex];

                                            if (eraser.Contains(inkPoint.Position))
                                            {
                                                stroke.Selected = true;

                                                if (pointIndex > 0)
                                                {
                                                    var points = inkPoints.GetRange(0, pointIndex);
                                                    var newStroke = new InkStrokeBuilder().CreateStrokeFromInkPoints(points, Matrix3x2.Identity);
                                                    newStroke.DrawingAttributes = stroke.DrawingAttributes;
                                                    newStroke.PointTransform = stroke.PointTransform;
                                                    strokeContainer.AddStroke(newStroke);
                                                }
                                                break;
                                            }
                                        }

                                        for (int pointIndex = inkPoints.Count - 1; pointIndex >= 0; pointIndex--)
                                        {
                                            var inkPoint = inkPoints[pointIndex];

                                            if (eraser.Contains(inkPoint.Position))
                                            {
                                                stroke.Selected = true;

                                                if (pointIndex < inkPoints.Count - 1)
                                                {
                                                    var points = inkPoints.GetRange(pointIndex + 1, inkPoints.Count - (pointIndex + 1));
                                                    var newStroke = new InkStrokeBuilder().CreateStrokeFromInkPoints(points, Matrix3x2.Identity);
                                                    newStroke.DrawingAttributes = stroke.DrawingAttributes;
                                                    newStroke.PointTransform = stroke.PointTransform;
                                                    strokeContainer.AddStroke(newStroke);
                                                }
                                                break;
                                            }
                                        }

                                        if (stroke.Selected)
                                        {
                                            strokeContainer.DeleteSelected();
                                        }
                                    }
                                }
                            }
                        }

                        this.lastPoint = args.CurrentPoint.Position;
                        args.Handled = true;
                        canvas.Invalidate();
                    }
                    break;
                case ToolbarMode.Lasso:
                    if (this.isBoundRect)
                    {
                        if (args.CurrentPoint.RawPosition.X > 0 && args.CurrentPoint.RawPosition.Y > 0 && args.CurrentPoint.RawPosition.X < this.ActualWidth && args.CurrentPoint.RawPosition.Y < this.ActualHeight)
                        {
                            this.lasso.Points.Add(args.CurrentPoint.RawPosition);
                        }
                    }
                    if (this.isSelectionDragging)
                    {
                        Rect boundingRect = Rect.Empty;
                        Point upperLeft = new Point(args.CurrentPoint.RawPosition.X - this.selectionOffset.Width, args.CurrentPoint.RawPosition.Y - this.selectionOffset.Height);
                        if (upperLeft.X > 0 && upperLeft.Y > 0 && upperLeft.X + this.selectionRectangle.Value.Width < this.ActualWidth && upperLeft.Y + this.selectionRectangle.Value.Height < this.ActualHeight)
                        {
                            Point point = new Point(args.CurrentPoint.RawPosition.X - this.selectionDragPoint.X, args.CurrentPoint.RawPosition.Y - this.selectionDragPoint.Y);
                            foreach (InkStrokeContainer item in this.strokes.ToArray())
                            {
                                Rect rect = item.MoveSelected(point);
                                if (rect.Width > 0 && rect.Height > 0)
                                {
                                    boundingRect = RectHelper.Union(boundingRect, rect);
                                }

                            }

                            this.DrawBoundingRect(boundingRect);
                        }

                        this.canvas.Invalidate();
                        this.selectionDragPoint = args.CurrentPoint.RawPosition;
                    }
                    break;
            }
        }

        private void DrawBoundingRect(Rect boundingRect)
        {
            this.SelectionCanvas.Children.Clear();

            if (boundingRect.Width <= 0 || boundingRect.Height <= 0)
            {
                return;
            }

            var rectangle = new Rectangle()
            {
                Stroke = new SolidColorBrush(Colors.Blue),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection() { 5, 2 },
                Width = boundingRect.Width,
                Height = boundingRect.Height
            };

            Canvas.SetLeft(rectangle, boundingRect.X);
            Canvas.SetTop(rectangle, boundingRect.Y);

            this.SelectionCanvas.Children.Add(rectangle);
            this.selectionRectangle = boundingRect;
        }

        private void UnprocessedInput_PointerLost(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (this.isErasing)
            {
                args.Handled = true;
            }
            this.isErasing = false;
        }

        private void UnprocessedInput_PointerExited(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (this.isErasing)
            {
                args.Handled = true;
            }
            this.isErasing = true;
        }

        private void UnprocessedInput_PointerPressed(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (args.CurrentPoint.Properties.IsRightButtonPressed && args.CurrentPoint.PointerDevice.PointerDeviceType == PointerDeviceType.Mouse)
            {
                this.ShowInkSelectionToolbar();
                return;
            }

            switch (this.toolbarMode)
            {
                case ToolbarMode.Erasing:
                    this.lastPoint = args.CurrentPoint.Position;
                    args.Handled = true;
                    this.isErasing = true;
                    eraser = new Rect(args.CurrentPoint.Position, new Size(30, 30));
                    break;
                case ToolbarMode.Lasso:
                    if (this.selectionRectangle != null)
                    {
                        if (this.selectionRectangle.Value.Contains(args.CurrentPoint.RawPosition))
                        {
                            this.selectionDragPoint = args.CurrentPoint.RawPosition;
                            this.selectionOffset = new Size(args.CurrentPoint.RawPosition.X - this.selectionRectangle.Value.X, args.CurrentPoint.RawPosition.Y - this.selectionRectangle.Value.Y);
                            this.isSelectionDragging = true;
                            return;
                        }

                        this.isClickedOutsizeSelection = true;
                    }
                    else
                    {
                        this.ClearDrawnBoundingRect();
                        this.lasso = new Polyline()
                        {
                            Stroke = new SolidColorBrush(Colors.Blue),
                            StrokeThickness = 1,
                            StrokeDashArray = new DoubleCollection() { 5, 2 },
                        };

                        this.lasso.Points.Add(args.CurrentPoint.RawPosition);
                        this.SelectionCanvas.Children.Add(this.lasso);
                        this.isBoundRect = true;
                    }
                    break;
            }

            if (this.InkSelectionToolbar.Visibility == Visibility.Visible)
            {
                this.InkSelectionToolbar.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearDrawnBoundingRect()
        {
            this.selectionRectangle = null;
            this.isClickedOutsizeSelection = false;
            if (this.SelectionCanvas.Children.Count > 0)
            {
                this.SelectionCanvas.Children.Clear();
                this.lasso = null;
            }
        }

        private void UnprocessedInput_PointerReleased(InkUnprocessedInput sender, PointerEventArgs args)
        {
            switch (this.toolbarMode)
            {
                case ToolbarMode.Erasing:
                    if (this.isErasing) args.Handled = true;
                    this.isErasing = false;
                    eraser = new Rect(args.CurrentPoint.Position, new Size(0, 0));
                    break;
                case ToolbarMode.Lasso:
                    if (this.isSelectionDragging)
                    {
                        this.isSelectionDragging = false;
                    }
                    else
                    {
                        if (this.isClickedOutsizeSelection)
                        {
                            if (this.InkSelectionToolbar.Visibility == Visibility.Collapsed)
                            {
                                this.ClearDrawnBoundingRect();
                            }
                        }
                        else
                        {
                            if (this.lasso != null)
                            {
                                Rect boundingRect = Rect.Empty;
                                this.lasso.Points.Add(args.CurrentPoint.RawPosition);
                                foreach (InkStrokeContainer item in this.strokes.ToArray())
                                {
                                    Rect rect = item.SelectWithPolyLine(this.lasso.Points);
                                    if (rect.Width > 0 && rect.Height > 0)
                                    {
                                        boundingRect = RectHelper.Union(boundingRect, rect);
                                    }
                                }

                                this.isBoundRect = false;
                                this.DrawBoundingRect(boundingRect);
                            }
                        }
                    }
                    break;
            }
        }

        private void OnInkToolbarAction(object sender, RoutedEventArgs e)
        {
            string option = (sender as Button).Name;

            switch (option)
            {
                case "Copy":
                case "Cut":
                    this.copyContainer = new InkStrokeContainer();
                    if (this.renderedStrokes.Any())
                    {
                        foreach (InkStroke stroke in this.renderedStrokes.Where(s => s.Selected))
                        {
                            this.copyContainer.AddStroke(stroke.Clone());
                        }

                        this.copyContainer.SelectWithPolyLine(this.lasso.Points);
                        this.copyContainer.CopySelectedToClipboard();
                        if (option == "Cut")
                        {
                            this.OnEraserClicked(sender, null);
                        }

                        this.canvas.Invalidate();
                    }
                    break;
                case "Paste":
                    //Unselects all previously selected areas
                    foreach (InkStrokeContainer container in this.strokes)
                    {
                        container.SelectWithLine(new Point(0, 0), new Point(0, 0));
                    }

                    //Fake paste to determine pasted content size
                    InkStrokeContainer pasteFakeContainer = new InkStrokeContainer();
                    Rect pastedContentArea = pasteFakeContainer.PasteFromClipboard(this.toolbarPosition);

                    //Paste it ensuring it fits client size
                    InkStrokeContainer pasteContainer = new InkStrokeContainer();
                    Point pastePoint = this.EnsureFit(this.toolbarPosition, pastedContentArea);
                    Rect pastedBoundingRect = pasteContainer.PasteFromClipboard(pastePoint);
                    this.DrawBoundingRect(pastedBoundingRect);

                    //Selects pasted ink
                    List<Point> pastedLasso = new List<Point>();
                    pastedLasso.Add(new Point(pastedBoundingRect.X, pastedBoundingRect.Y));
                    pastedLasso.Add(new Point(pastedBoundingRect.X + pastedBoundingRect.Width, pastedBoundingRect.Y));
                    pastedLasso.Add(new Point(pastedBoundingRect.X + pastedBoundingRect.Width, pastedBoundingRect.Y + pastedBoundingRect.Height));
                    pastedLasso.Add(new Point(pastedBoundingRect.X, pastedBoundingRect.Y + pastedBoundingRect.Height));
                    pastedLasso.Add(new Point(pastedBoundingRect.X, pastedBoundingRect.Y));
                    pasteContainer.SelectWithPolyLine(pastedLasso);
                    this.strokes.Add(pasteContainer);
                    this.canvas.Invalidate();
                    //Se lasso matching pasted content
                    this.lasso = new Polyline()
                    {
                        Stroke = new SolidColorBrush(Colors.Blue),
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection() { 5, 2 },
                    };
                    foreach (Point lassoPoint in pastedLasso)
                    {
                        this.lasso.Points.Add(lassoPoint);
                    }
                    //We move into lasso mode to let user move pasted selection
                    //this.OnLassoChecked(this,null);
                    this.hasPasted = true;
                    break;
                case "Delete":
                    this.OnEraserClicked(sender, null);
                    break;
            }

            this.InkSelectionToolbar.Visibility = Visibility.Collapsed;
        }
        private Point EnsureFit(Point point, Rect size)
        {
            var x = point.X;
            var y = point.Y;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x + size.Width > this.ActualWidth - 5) x = this.ActualWidth - size.Width - 5;
            if (y + size.Height > this.ActualHeight) y = this.ActualHeight - size.Height;

            return new Point(x, y);
        }
        #endregion

        #region Stroke drawing

        private void InkPresenter_StrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
        {
            this.pendingDry = this.inkSynchronizer.BeginDry();
            var container = new InkStrokeContainer();
            foreach (var stroke in this.pendingDry)
            {
                container.AddStroke(stroke.Clone());
            }

            this.strokes.Add(container);
            canvas.Invalidate();
        }

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            foreach (var item in this.strokes)
            {
                IReadOnlyList<InkStroke> strokes = item.GetStrokes();
                this.renderedStrokes.AddRange(strokes);
                args.DrawingSession.DrawInk(strokes);
            }

            if (this.pendingDry != null && this.deferredDryDelay == 0)
            {
                this.deferredDryDelay = 1;
                CompositionTarget.Rendering += this.OnDeferEndDry;
            }

            if (this.isErasing)
                args.DrawingSession.DrawRectangle(eraser, ColorHelper.FromArgb(255, 255, 0, 0));
        }

        private void OnDeferEndDry(object sender, object e)
        {
            if (this.deferredDryDelay > 0)
            {
                this.deferredDryDelay--;
            }
            else
            {
                CompositionTarget.Rendering -= this.OnDeferEndDry;
                this.pendingDry = null;
                this.inkSynchronizer.EndDry();
            }
        }
        #endregion

        private async void OnShare(object sender, RoutedEventArgs e)
        {
            var activeTool = InkToolbar.ActiveTool;

            // Show the share UI
            DataTransferManager.ShowShareUI();

            await Task.Delay(TimeSpan.FromSeconds(0.1));

            // reset the active tool after pressing the share button
            InkToolbar.ActiveTool = activeTool;
        }

        /// <summary>
        /// When the ScrollViewer zooms in or out, we update DpiScale on our CanvasVirtualControl
        /// to match. This adjusts its pixel density to match the current zoom level. But its size
        /// in dips stays the same, so layout and scroll position are not affected by the zoom.        
        /// /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <remarks>Adapted from Win2D Gallery Mandelbrot sample at
        /// <![CDATA[https://github.com/Microsoft/Win2D/blob/master/samples/ExampleGallery/Shared/Mandelbrot.xaml.cs]]>
        /// </remarks>
        private void OnScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            // Cancel out the display DPI, so our fractal always renders at 96 DPI regardless of display
            // configuration. This boosts performance on high DPI displays, at the cost of visual quality.
            // For even better performance (but lower quality) this value could be further reduced.
            float dpiAdjustment = 96 / displayDpi;

            // Adjust DPI to match the current zoom level.
            float dpiScale = dpiAdjustment * ScrollViewer.ZoomFactor;

            // To boost performance during pinch-zoom manipulations, we only update DPI when it has
            // changed by more than 20%, or at the end of the zoom (when e.IsIntermediate reports false).
            // Smaller changes will just scale the existing bitmap, which is much faster than recomputing
            // the fractal at a different resolution. To trade off between zooming perf vs. smoothness,
            // adjust the thresholds used in this ratio comparison.

            var ratio = canvas.DpiScale / dpiScale;

            if (e == null || !e.IsIntermediate || ratio <= 0.8 || ratio >= 1.25)
            {
                canvas.DpiScale = dpiScale;
            }
        }
        #endregion
    }
}