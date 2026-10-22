namespace IPhoneMirror.App.Updater;

/// <summary>
/// Where this build gets its updates. OpenMirror is a fork of
/// RayrenSX/iPhoneMirror; installing an upstream release would replace the
/// fork's home window and Japanese UI, so updates come from the fork only.
/// Optional components that the fork does not repackage (UxPlay) still come
/// from the upstream releases.
/// </summary>
internal static class ReleaseRepository
{
    internal const string Slug = "manato003/OpenMirror";
    internal const string UpstreamSlug = "RayrenSX/iPhoneMirror";
    internal const string WebRoot = "https://github.com/" + Slug;
    internal const string UpstreamWebRoot = "https://github.com/" + UpstreamSlug;
    internal const string RawRoot = "https://raw.githubusercontent.com/" + Slug;
}
