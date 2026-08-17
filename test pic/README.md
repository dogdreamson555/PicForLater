# Built-in API capability-test image

`cat.jpg` is a repository-owned test fixture and a small embedded product asset.
It is not copied from a PicForLater library and contains no user content.

- Purpose: the explicit `API · Image` connection test. Vision endpoints receive
  this fixed image instead of a user image before a provider profile is enabled.
- Dimensions: 640 × 960 JPEG.
- File size: 61,868 bytes.
- SHA-256: `9afff550a763f949ecc3b39dd5a7d17c9225e40e0405da93330fb0a2487aa641`.
- License: free to use under the [Unsplash License](https://unsplash.com/license),
  as supplied and approved for redistribution by the repository owner.
- Source: [unsplash.com/photos/white-and-brown-long-fur-cat-ZCHj_2lJP00](https://unsplash.com/photos/white-and-brown-long-fur-cat-ZCHj_2lJP00)

The image is embedded in `PicForLater.Analysis.dll`; connection testing never
opens the product database or enumerates managed images. The UI discloses that
the selected provider receives this image and that the request may be billed.
