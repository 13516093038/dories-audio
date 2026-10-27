# Dories Audio

```csharp
// GameEntry 预制体的 Audio 节点已配好 YooAssetAudioClipProvider；也可在 Inspector 的 Clip Provider 下拉切换或代码指定
var audio = FindObjectOfType<AudioEntity>();

await audio.PreloadAsync("Click", keepAlive: true);          // 高频音效预加载常驻
await audio.PlayMusicAsync("BgmA", fade: 1f);                // 与当前音乐交叉淡入淡出，同一首不重播
audio.StopMusic(fade: 1f);

int id = await audio.PlaySoundAsync("Click");                // 2D；返回 0 表示失败或超过并发上限
await audio.PlaySoundAtAsync("Boom", pos);                   // 3D
int loop = await audio.PlaySoundAsync("Engine", loop: true);
audio.StopSound(loop);

audio.SetVolume(AudioChannel.Music, 0.5f);
audio.SetMute(AudioChannel.Sound, true);

// 其它包：可选传 packageName，同一地址在不同包里分别缓存
await audio.PlaySoundAsync("Roar", packageName: "DLC1");
```

- 音乐两路 AudioSource 交替，淡入淡出用 unscaled 时间；连续请求以最后一次为准。
- 音效 AudioSource 复用，播完自动回收；`AudioListener.pause` 期间不回收。
- AudioClip 按（包名, 地址）缓存并引用计数，无人使用且闲置超过 `_clipIdleReleaseSeconds` 卸载；`keepAlive` 的除外；场景卸载后（默认）释放无人使用的。
- 音量/静音可在 Inspector 调，Play Mode 下即时生效；可选 Mixer 分组。
- Inspector 在 Play Mode 显示当前音乐、播放中音效与 AudioClip 缓存。
- 自定义加载来源：实现 `IAudioClipProvider`，标 `[Serializable]` 并保留无参构造，就会出现在 Inspector 下拉里。
