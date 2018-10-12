# InkToolbar and Custom Dry Ink
Windows UWP Sample that demonstrate how to use the InkToolbar Control with a custom Dry Ink CanvasControl

See [this article](https://blogs.msdn.microsoft.com/synergist/2016/08/26/using-the-inktoolbar-with-custom-dry-ink-in-windows-anniversary-edition/) 
for a full explanation.
## Features Demonstrated
- Implementing custom dry mode for ink
- Drawing ink using Win2D and effects
- Implementing ink erasing with the InkToolbar control with custom dry Ink
- Adding a custom button to the InkToolbar
- Rendering Win2D to be used in a Share contract
- Simultaneous pen and touch with the ScrollViewer control
- Adapts DPI when zooming so that the rendering isn't pixelated when zoomed in.

# Point Eraser Implementation Guidance

First off, Pixel accurate erasing is not supported at present in the sense that our ink rendering model does not produce arbitrarily irregularly shaped strokes; this means that partial cutouts of strokes and things like that are not possible.  The only way to achieve something like that at present is to use the white (or whatever the background color) ink as eraser trick, but of course that solution has its own drawbacks and limitations.

That said, you can think of the algorithm as being split into the following parts.

1.	Eraser Path Model: Constructing an eraser contour from input.
2.	Hit testing: Ascertaining which, if any, strokes intersect with the eraser contour.
3.  Intersection Finding: Determining at which position the strokes intersect with the eraser contour.
4.  Stroke Splitting: Splitting the stroke that intersects with the eraser into 0, 1, 2, or more sub-strokes.

## Erase Path Model
The model of the eraser path we use internally is shown below.  Using a Rectangular eraser shape the path swept by the eraser from time t-1 to time t defines a hexagonal contour as shown below (for a perfectly vertical or horizontal path it would be rectangular).  The strokes that intersect with this path are those that are “hit” by the eraser. 

Image goes here

An alternative model that is simpler and can be used is something like the picture below that consists of a sequence of interpolated partially overlapping rectangles.  The main disadvantage of this approach is that it results in a stair-step pattern in the erase – an effect that is clear when erasing a group of strokes.  Another disadvantage is that the number of operations required goes up as the number of interpolated rectangles increases; the number of interpolated rectangles increases as the speed of the eraser increases, the eraser size decreases, and the degree of desired overlap increases.  On the other hand it works well enough in most cases and you get to use simple axis-aligned rectangles for your hit testing and intersection finding.

Image goes here

With some work, it is possible to do better than these eraser path models;  for example a smooth interpolated path between eraser input points can be generated rather than simple using line segments as is done above.  You could also have a model that changes the size of the eraser in response to pressure or velocity.

## Hit Testing
The goal of hit testing is to find which strokes intersect with the eraser contour.  The specifics depend on your eraser contour, but the basic idea is to use progressively more strict filters.  For example, as a first filter you could find all those strokes whose bounding boxes intersect the eraser contour’s bounding box.  The next filter might be to test whether the stroke’s bounding box intersects the actual eraser contour.  And then the next filter might  be to test each InkPoint to InkPoint line segment to see whether it intersects with the eraser contour.

The above hit testing scheme implies that an eraser has “hit” a stroke only when it intersects the middle of the stroke and doesn’t take into account the stroke’s thickness, which is the easiest way to go.  It is possible to choose differently and consider a stroke “hit” if the eraser contacts the outside edge of the stroke or use some other criteria.

## Find Intersections
This is really an extension of hit testing, but now the task is to find exactly where the Eraser Contour intersects the InkStroke.

Again, test each InkStroke line segment, in order, for intersection with the Eraser Contour but this time record the points of intersection in pairs corresponding to entrance and exit from the eraser contour (a special but common case being that one or the other of these does not exist [ex. erasing the beginning/ending of a stroke]).  For example, if an (Enter eraser contour) intersection is found half way between InkPoint 23 and 24, and the next (Exit eraser contour) intersection is found at 26 then the output intersection pair is (23.5, 26.0).

Note that an eraser contour could intersect a stroke an arbitrary number of times.  The intersection finding logic should account for this.

## Split Strokes
Once the points of intersection are found new “split strokes” are created whose bounds are the points of intersection found in the previous step.  This is the simplest approach and works ok, but improvements are possible; one minor problem is that each of the new split strokes endpoints may overlap since the stroke extends beyond these intersection points by half a pen tip width.  This is a noticeable effect especially for larger pen tips and is undesirable.  It is possible (but not trivial) to adjust intersected points to account for the estimated pen tip dimensions at that point and eliminate this effect.  
