# Post-production compositing: depth video and tilt content in Open Brush

Original design brief for `POSTPROD_PREVIEW_PLAN.md` (alongside this file), moved
here from `D:\Alison-Goodyear-Postproduction\depth_work\` and reformatted for
readability. Content unchanged from the original.

## The basic principle

- Outside of Open Brush, a user creates a video and corresponding metric depth map.
  In our worked example we have done this by using a Blackmagic recording with
  ChArUco board calibration to obtain virtual camera extrinsics and intrinsics.
- We can use this with the Open Brush API to place a camera in Open Brush. We have
  applied lens shift and FOV to the camera in Open Brush but not realistic lens
  distortion.
- We can create an undistorted view of the video footage to use with Open Brush. The
  undistorted view clips off some of the footage, so we will need to create a
  slightly larger camera frame in Open Brush so that there is enough information
  captured to be able to distort it back to composite the original camera — we have
  previously managed to calculate that.
- For now we can just work with the undistorted process and then add in the
  oversizing and distorting as a second stage.
- Open Brush accesses files stored here: `C:\Users\rich\OneDrive\Documents\Open Brush`.
  We will need to add our video colour and depth frames into a folder here (perhaps
  these should be in the same format that Open Brush exports its colour and depth
  maps in?). However there is insufficient C drive space, so the folder we put them
  in will need to be symlinked or mapped to a folder on the D drive.

## Layers

- The footage and the tilt contain a dance performed by three dancers wearing
  headsets. Each dancer's strokes appear in their own layer in the tilt file.
- When loaded, the API allows for the tilt file to be scrubbed through, paused, and a
  snapshot of colour and depth taken. It also allows tilt layers to be hidden.
- However, upon load, it seems that all of the dancers' strokes are being merged into
  one layer. We need dancer layers to be preserved so that we can show or hide these
  in Open Brush.

## Temporal alignment

- At the moment the dancers and tilt are not in sync. It looks like there is a delay
  or latency of a few frames. This might be one global delay for all three dancers,
  or it might be a slightly different delay per dancer/layer.
- We need the API to be able to take a delay value per layer plus a global value. The
  tilt may be ahead of the video, in which case the delay value is negative.

## Spatial alignment

- At the moment the dancers' strokes do not completely line up with their hand
  positions in the video. A global transform was applied to position the camera in
  broadly the correct location. We do not have an exact calculated transform;
  however, the ground plane and scale look consistent between video and tilt.
- The transform may be a global transform that applies to all dancers (layers).
  Additionally we might need a transform per dancer — the dancers were using a form
  of colocation, so they may be slightly misaligned.

## Purpose of the API changes

- The purpose of our API changes is to assist in correcting these temporal and
  spatial misalignments. I envisage that we will create the API functionality to do
  this, then probably create a web UI to add to the Open Brush examples.
- The API will allow the temporal and spatial global offsets to be declared and
  changed, plus similar offsets per layer.

## Preview UX

- I imagine that the user will scrub through the footage and be able to preview
  individual frames either in naive (Open Brush over the top) or depth-aware
  (comparing the depth maps) mode.
- I imagine the scrub as a slider but with the ability to specify frames/times
  specifically.
- I imagine layers listed, with a show/hide toggle, along with spaces to edit the
  temporal and spatial offset for that layer.
- At the moment we expect the temporal offset to be moving on the x and z Unity axis
  and potentially rotation around the y axis. Capability for scale and other rotation
  needs to be there just in case. Perhaps the API needs both Euler and quaternion
  input (see our other API amendments to get to this stage).

## Camera view

- At the moment, the Open Brush display camera does not show the view from the
  spectator/rendering camera — instead it shows from the player camera. Perhaps this
  should follow the spectator camera in postproduction mode to give us a live preview
  of what we are working on? Or maybe an option to lock the two together.
- It would be nice to see the frame composite in Open Brush rather than in the Open
  Brush API/web interface.

## Persistence and render

- Once we have set the temporal and spatial offsets correctly, we want this
  information to be saved and the ability to trigger a render.
- Previously we rendered from a script outside Open Brush, repeatedly calling the API
  snapshot. We think this loop for the full render would be more performant inside
  Open Brush.
