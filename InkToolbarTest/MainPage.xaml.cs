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

		private Flyout _eraseAllFlyout;

		private InkSynchronizer _inkSynchronizer;

		private bool _isErasing;

		private Point _lastPoint;

		private int _deferredDryDelay;

		private float displayDpi;
		private ToolbarMode toolbarMode;
		#endregion

		#region multi stylus support
		// please  verify if for a non async method lock statement works on uwp as on desktop or not
		//private AsyncLock _locker = new AsyncLock();

		//used to store InkDraingAttribute of esch surface hub stylus
		private Dictionary<int, InkDrawingAttributes> _stylusAttributes = new Dictionary<int, InkDrawingAttributes>();
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
				penButton.PointerExited += ConfigControl_PointerExited;
				Flyout buttonlFlyout = FlyoutBase.GetAttachedFlyout(penButton) as Flyout;
				if (buttonlFlyout != null)
				{
					var configControl = buttonlFlyout.Content as InkToolbarPenConfigurationControl;

					if (configControl != null)
						configControl.PointerExited += ConfigControl_PointerExited;
				}
			}

			// TODO: unsubscribe to all those events
			if (highlighterButton != null)
			{
				highlighterButton.PointerExited += ConfigControl_PointerExited;
				Flyout buttonlFlyout = FlyoutBase.GetAttachedFlyout(highlighterButton) as Flyout;
				if (buttonlFlyout != null)
				{
					var configControl = buttonlFlyout.Content as InkToolbarPenConfigurationControl;

					if (configControl != null)
						configControl.PointerExited += ConfigControl_PointerExited;
				}
			}

			// TODO: unsubscribe to all those events
			if (pencilButton != null)
			{
				pencilButton.PointerExited += ConfigControl_PointerExited;
				Flyout buttonlFlyout = FlyoutBase.GetAttachedFlyout(pencilButton) as Flyout;
				if (buttonlFlyout != null)
				{
					var configControl = buttonlFlyout.Content as InkToolbarPenConfigurationControl;

					if (configControl != null)
						configControl.PointerExited += ConfigControl_PointerExited;
				}
			}

			// Phase 1 (ConfigControl_PointerExited): Every time user select (or not) a new property we sotre it for its own unique stylus id
			// Phase 2 (unprocessedInput.PointerHovered): when the user starts inking just a moment before the ink starts we get the unique stylus id from the PointerHovered
			// we look into _stylusAttributes for the InkDrawingAttributes of that stylus and we apply to the InkCanvas.InkPresenter.UpdateDefaultDrawingAttributes
			#endregion


			// 1. Activate custom drawing 
			_inkSynchronizer = InkCanvas.InkPresenter.ActivateCustomDrying();

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

			_eraseAllFlyout = FlyoutBase.GetAttachedFlyout(eraser) as Flyout;

			if (_eraseAllFlyout != null)
			{
				var button = _eraseAllFlyout.Content as Button;

				if (button != null)
				{
					var newButton = new Button();
					newButton.Style = button.Style;
					newButton.Content = button.Content;

					newButton.Click += EraseAllInk;
					_eraseAllFlyout.Content = newButton;
				}
			}
		}

		#region multi stylus support

		// Phase 1:
		// Every time user select (or not) a new property we sotre it for its own unique stylus id
		private void ConfigControl_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			int uniqueStylusId = GetUniqueStylusId(PointerPoint.GetCurrentPoint(e.Pointer.PointerId));
			//using (var releaser = await _locker.LockAsync())
			//{
			// if user is selecting the inktoolbar with a stylusu where a DrawingAttributes was already associated with, we delete that object
			// so that _stylusAttributes we have the uniqueStylusId but not the DrawingAttributes that will be added on Phase 2
			// if it was the first time we just add to the _stylusAttributes the uniqueStylusId
			if (_stylusAttributes.ContainsKey(uniqueStylusId))
				_stylusAttributes[uniqueStylusId] = InkCanvas.InkPresenter.CopyDefaultDrawingAttributes();
			else
				_stylusAttributes.Add(uniqueStylusId, InkCanvas.InkPresenter.CopyDefaultDrawingAttributes());
			//}

			Debug.WriteLine($"ConfigControl_PointerEntered Unique Stylus Id: {uniqueStylusId}");
		}

		// Phase 2: 
		// this method need to be super veloce because this happens, we have the chance to change the color but the inking will start even this isn't finished
		// so it can happens that if two stylust start exactly at the same time inking one of the two whon't have it's own DrawingAttributes stored in _stylusAttributes
		// because the ininkg process has started and we din't changed the DrawingAttributes 
		private void UnprocessedInput_PointerHovered(InkUnprocessedInput sender, PointerEventArgs e)
		{
			
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

		private void EraseAllInk(object sender, RoutedEventArgs e)
		{
			this.strokes.Clear();
			this.canvas.Invalidate();

			_eraseAllFlyout.Hide();
		}

		private void Eraser_Checked(object sender, RoutedEventArgs e)
		{
			//var unprocessedInput = InkCanvas.InkPresenter.UnprocessedInput;
			//unprocessedInput.PointerPressed += UnprocessedInput_PointerPressed;
			//unprocessedInput.PointerMoved += UnprocessedInput_PointerMoved;
			//unprocessedInput.PointerReleased += UnprocessedInput_PointerReleased;
			//unprocessedInput.PointerExited += UnprocessedInput_PointerExited;
			//unprocessedInput.PointerLost += UnprocessedInput_PointerLost;

			InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.None;
		}

		private void Eraser_Unchecked(object sender, RoutedEventArgs e)
		{
			//var unprocessedInput = InkCanvas.InkPresenter.UnprocessedInput;

			//unprocessedInput.PointerPressed -= UnprocessedInput_PointerPressed;
			//unprocessedInput.PointerMoved -= UnprocessedInput_PointerMoved;
			//unprocessedInput.PointerReleased -= UnprocessedInput_PointerReleased;
			//unprocessedInput.PointerExited -= UnprocessedInput_PointerExited;
			//unprocessedInput.PointerLost -= UnprocessedInput_PointerLost;

			InkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
		}

		private void UnprocessedInput_PointerMoved(InkUnprocessedInput sender, PointerEventArgs args)
		{
			switch (this.toolbarMode)
			{
				case ToolbarMode.Erasing:
					if (!this.isErasing) return;
					bool invalidate = false;
					foreach (InkStrokeContainer item in this.strokes.ToArray())
					{
						Rect rect = item.SelectWithLine(this.lastPoint, args.CurrentPoint.Position);
						if (rect.IsEmpty) continue;
						if (rect.Width * rect.Height > 0)
						{
							this.strokes.Remove(item);
							invalidate = true;
						}
					}

					this.lastPoint = args.CurrentPoint.Position;
					args.Handled = true;
					if (invalidate)
					{
						this.canvas.Invalidate();
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
				InkSelectionToolbar.Visibility=Visibility.Visible;
				return;
			}

			switch (this.toolbarMode)
			{
				case ToolbarMode.Erasing:
					this.lastPoint = args.CurrentPoint.Position;
					args.Handled = true;
					this.isErasing = true;
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

		private void InkPresenter_StrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
		{
			this.pendingDry = _inkSynchronizer.BeginDry();
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
				_deferredDryDelay = 1;
				CompositionTarget.Rendering += this.OnDeferEndDry;
			}
		}
		

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

		private void OnLassoClicked(object sender, RoutedEventArgs e)
		{

		}

		private void OnInkToolbarAction(object sender, RoutedEventArgs e)
		{
			throw new NotImplementedException();
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
				_inkSynchronizer.EndDry();
			}
		}

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
	}
}