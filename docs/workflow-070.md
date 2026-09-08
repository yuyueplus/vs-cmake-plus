# 0.7.0 — Create a target, then add source files

1. Select **New Target…**, fill in its details, and review the changes.
2. **Apply Changes** creates the target files and saves the modified parent CMake script as UTF-8 without BOM. If that script already contains unsaved edits, a confirmation identifies that they will be saved together. Other documents are not saved.
3. The window shows **Waiting for VS configuration…**. Target editing is temporarily disabled until a newer successful File API model has arrived and its inputs are current. The plugin does not run a separate CMake process. If VS automatic configuration is disabled or configuration fails, reconfigure through VS.
4. Right-click the new directory and choose **New Source File…**. The default path is relative to that directory, for example `my_target/new_file.cpp`.
5. The default target follows the explicitly selected target, an existing file's unique owner, or the closest unique target declaration directory. When several targets match, select one explicitly.

The same save workflow applies to source creation and target reference edits. Script undo remains available, but it does not remove created files; save and reconfigure after undo. A failed save leaves the editor changes available for manual saving and reports the failure.

Verification: 107 core checks passed, including six checks for directory defaults, nested directories, shared declarations, explicit target selection, file ownership, and external path fallback. SDK builds cover VS 2022 and VS 2026. The full save/configuration sequence has not been verified in the user's daily VS instance.
