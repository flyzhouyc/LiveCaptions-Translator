using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace LiveCaptionsTranslator.models
{
    public class StreamingTranslation
    {
        private static readonly StreamingTranslation _instance = new StreamingTranslation();
        private readonly Dictionary<string, List<IStreamingTranslationObserver>> _observers = new();
        private readonly SemaphoreSlim _observerLock = new(1);

        public static StreamingTranslation Instance => _instance;

        private StreamingTranslation() { }

        public interface IStreamingTranslationObserver
        {
            void OnPartialTranslation(string text, string partialTranslation);
            void OnTranslationComplete(string text, string finalTranslation);
            void OnTranslationError(string text, string error);
        }

        public async Task RegisterObserverAsync(string text, IStreamingTranslationObserver observer)
        {
            await _observerLock.WaitAsync();
            try
            {
                if (!_observers.ContainsKey(text))
                {
                    _observers[text] = new List<IStreamingTranslationObserver>();
                }
                _observers[text].Add(observer);
            }
            finally
            {
                _observerLock.Release();
            }
        }

        public async Task UnregisterObserverAsync(string text, IStreamingTranslationObserver observer)
        {
            await _observerLock.WaitAsync();
            try
            {
                if (_observers.ContainsKey(text))
                {
                    _observers[text].Remove(observer);
                    if (_observers[text].Count == 0)
                    {
                        _observers.Remove(text);
                    }
                }
            }
            finally
            {
                _observerLock.Release();
            }
        }

        public async Task StreamTranslateAsync(string text, CancellationToken cancellationToken = default)
        {
            var partialResult = new StringBuilder();
            var words = text.Split(' ');
            var currentPhrase = new StringBuilder();
            var minTranslationLength = App.Settings.MinTranslationLength;
            var maxSyncInterval = App.Settings.MaxSyncInterval;
            var wordCount = 0;

            try
            {
                for (int i = 0; i < words.Length; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    currentPhrase.Append(words[i]).Append(' ');
                    wordCount++;

                    // 根据配置参数决定何时触发翻译
                    bool shouldTranslate = false;
                    string phrase = currentPhrase.ToString().Trim();

                    // 检查最小翻译长度
                    if (phrase.Length >= minTranslationLength)
                    {
                        shouldTranslate = true;
                    }
                    // 检查最大同步间隔
                    else if (wordCount >= maxSyncInterval)
                    {
                        shouldTranslate = true;
                    }
                    // 检查句子结束标记
                    else if (words[i].EndsWith('.') || words[i].EndsWith('!') || words[i].EndsWith('?'))
                    {
                        shouldTranslate = true;
                    }
                    // 检查是否到达文本末尾
                    else if (i == words.Length - 1)
                    {
                        shouldTranslate = true;
                    }

                    if (shouldTranslate)
                    {
                        string translation = await TranslateAPI.TranslateFunc(phrase);
                        if (!string.IsNullOrEmpty(translation))
                        {
                            partialResult.Clear(); // 使用最新的翻译替换之前的结果
                            partialResult.Append(translation);
                            await NotifyObserversPartialAsync(text, partialResult.ToString().Trim());
                            wordCount = 0; // 重置词计数
                        }
                    }
                }

                // 最终完整翻译
                if (text.Length >= minTranslationLength)
                {
                    string finalTranslation = await TranslateAPI.TranslateFunc(text);
                    await NotifyObserversCompleteAsync(text, finalTranslation);
                }
            }
            catch (Exception ex)
            {
                await NotifyObserversErrorAsync(text, ex.Message);
                throw;
            }
        }

        private async Task NotifyObserversPartialAsync(string text, string partialTranslation)
        {
            await _observerLock.WaitAsync();
            try
            {
                if (_observers.TryGetValue(text, out var observers))
                {
                    foreach (var observer in observers)
                    {
                        observer.OnPartialTranslation(text, partialTranslation);
                    }
                }
            }
            finally
            {
                _observerLock.Release();
            }
        }

        private async Task NotifyObserversCompleteAsync(string text, string finalTranslation)
        {
            await _observerLock.WaitAsync();
            try
            {
                if (_observers.TryGetValue(text, out var observers))
                {
                    foreach (var observer in observers)
                    {
                        observer.OnTranslationComplete(text, finalTranslation);
                    }
                }
            }
            finally
            {
                _observerLock.Release();
            }
        }

        private async Task NotifyObserversErrorAsync(string text, string error)
        {
            await _observerLock.WaitAsync();
            try
            {
                if (_observers.TryGetValue(text, out var observers))
                {
                    foreach (var observer in observers)
                    {
                        observer.OnTranslationError(text, error);
                    }
                }
            }
            finally
            {
                _observerLock.Release();
            }
        }

        public void Dispose()
        {
            _observerLock?.Dispose();
        }
    }
}
