# Demo Scenes (Judge Mode)

Bundled offline scenes live here as `.remotion` packages and are loaded
without any server. On first launch `LibraryManager` copies them into the
in-app Library and shows them as READY.

- File format: the `.remotion` zip produced by the server
  (`scene.json` + `timeline.json`; final builds add SAM 3D `.glb` assets).
- Large `.remotion` files are git-ignored — ship them inside the APK's
  StreamingAssets, not in git history.
- With **Demo Mode ON**, the Library shows only these bundled ready scenes
  and hides server errors. This is the judge configuration:
  Sideload -> Launch -> Basketball Crossover demo -> Enter Virtual Scene.
