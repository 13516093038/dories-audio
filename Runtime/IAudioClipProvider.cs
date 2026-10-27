#if DORIES_UNITASK_SUPPORT
using AudioClipTask = Cysharp.Threading.Tasks.UniTask<UnityEngine.AudioClip>;
#else
using AudioClipTask = System.Threading.Tasks.Task<UnityEngine.AudioClip>;
#endif

namespace Dories.Audio.Runtime
{
    /// <summary>
    /// AudioClip 加载来源。每次成功的 LoadAsync 对应一次 Unload（引用计数语义）。
    /// packageName 为空表示由实现决定用哪个包。
    /// </summary>
    public interface IAudioClipProvider
    {
        AudioClipTask LoadAsync(string packageName, string location);
        void Unload(string packageName, string location);
    }
}
