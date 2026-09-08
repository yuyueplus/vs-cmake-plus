# English UI — 0.6.1

Open the extension using **Tools → CMake Plus**. Use **Tools → Create CMake Project…** or the toolbar to create a project.

The toolbar and context menu expose these actions:

- **New Source File…** → **Preview Changes** → **Apply Changes**. Save the CMake script and reconfigure in VS.
- **New Target…**, **Add Existing Files…**, and **Remove Target References…** manage target membership.
- **New Directory…**, **Rename…**, and **Move To…** manage files and directories.
- **Delete to Recycle Bin…** previews file deletion and CMake reference changes before confirmation.
- **Recover Last File Operation…** restores a previous operation. For deletion, restore the files from Windows Recycle Bin first.
- **Open Target Declaration** opens the target's CMake script.

Use **Files** / **Targets** to switch views, **Search files or paths…** to search, and **Workspace Settings** to configure directories and exclusion rules.

All extension-authored UI messages are English. File names, project contents, CMake output, operating-system dialogs, and host-generated exception messages retain their original language. This release does not translate project files or change operation behavior.
