using System.Runtime.CompilerServices;

// the URP renderer feature and the HDRP custom post-process run the chain of the camera's component
[assembly: InternalsVisibleTo("LolaRetro.URP")]
[assembly: InternalsVisibleTo("LolaRetro.HDRP")]
