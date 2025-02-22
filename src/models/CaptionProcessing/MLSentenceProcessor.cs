using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using System.Threading.Tasks;

namespace LiveCaptionsTranslator.models.CaptionProcessing
{
    public class SentenceData
    {
        [LoadColumn(0)]
        public string Text { get; set; }
        
        [LoadColumn(1)]
        public bool IsSentenceEnd { get; set; }
    }

    public class SentencePrediction
    {
        [ColumnName("PredictedLabel")]
        public bool IsSentenceEnd { get; set; }
        
        [ColumnName("Score")]
        public float Probability { get; set; }
    }

    public class MLSentenceProcessor : SentenceProcessor
    {
        private static readonly MLContext _mlContext = new MLContext(seed: 0);
        private static ITransformer _model;
        private static PredictionEngine<SentenceData, SentencePrediction> _predictionEngine;
        
        // 动态等待时间相关
        private const int MIN_WINDOW_SIZE = 3;
        private const int MAX_WINDOW_SIZE = 10;
        private readonly Queue<(DateTime timestamp, int wordCount, int charCount)> _speechRateWindow = new();
        private static readonly TimeSpan MIN_WAIT_TIME = TimeSpan.FromSeconds(0.3);
        private static readonly TimeSpan MAX_WAIT_TIME = TimeSpan.FromSeconds(1.5);
        private double _currentSpeechRate = 0; // 词/秒
        private double _currentCharRate = 0; // 字符/秒
        private DateTime _lastActivityTime = DateTime.MinValue;
        private int _currentWindowSize = MIN_WINDOW_SIZE;

        public double GetCurrentSpeechRate() => _currentSpeechRate;
        public double GetCurrentCharRate() => _currentCharRate;

        public MLSentenceProcessor()
        {
            InitializeModel();
        }

        private void InitializeModel()
        {
            // 创建训练数据
            var trainingData = new List<SentenceData>
            {
                new SentenceData { Text = "Hello", IsSentenceEnd = false },
                new SentenceData { Text = "world", IsSentenceEnd = false },
                new SentenceData { Text = "!", IsSentenceEnd = true },
                // 添加更多训练数据...
            };

            // 创建和训练模型
            var pipeline = _mlContext.Transforms.Text.FeaturizeText("Features", "Text")
                .Append(_mlContext.BinaryClassification.Trainers.SdcaLogisticRegression());

            var trainingDataView = _mlContext.Data.LoadFromEnumerable(trainingData);
            _model = pipeline.Fit(trainingDataView);
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<SentenceData, SentencePrediction>(_model);
        }

        public override bool IsCompleteSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            // 首先使用基本规则
            if (base.IsCompleteSentence(text)) return true;

            // 然后使用ML模型进行预测
            var prediction = _predictionEngine.Predict(new SentenceData { Text = text });
            return prediction.IsSentenceEnd && prediction.Probability > 0.8f;
        }

        public TimeSpan GetDynamicWaitTime()
        {
            var timeSinceLastActivity = DateTime.Now - _lastActivityTime;
            
            // 如果超过5秒没有活动，使用最小等待时间
            if (timeSinceLastActivity.TotalSeconds > 5)
            {
                return MIN_WAIT_TIME;
            }

            // 使用字符率而不是词率来计算等待时间
            if (_currentCharRate <= 0) return TimeSpan.FromSeconds(0.5);

            // 动态基准值：慢速时使用较低的基准
            double baselineRate = _currentCharRate < 5 ? 3.0 : 10.0;
            double normalizedRate = Math.Min(_currentCharRate / baselineRate, 1.0);

            // 使用指数函数使等待时间变化更平滑
            double factor = Math.Exp(-normalizedRate);
            double waitTime = MIN_WAIT_TIME.TotalSeconds + 
                (MAX_WAIT_TIME.TotalSeconds - MIN_WAIT_TIME.TotalSeconds) * factor;

            return TimeSpan.FromSeconds(Math.Min(waitTime, 1.5));
        }

        public void UpdateSpeechRate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var now = DateTime.Now;
            _lastActivityTime = now;

            var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, 
                StringSplitOptions.RemoveEmptyEntries);
            var wordCount = words.Length;
            var charCount = text.Count(c => !char.IsWhiteSpace(c));

            _speechRateWindow.Enqueue((now, wordCount, charCount));

            // 动态调整窗口大小
            if (_currentCharRate > 0)
            {
                // 快速输入时增加窗口，慢速输入时减少窗口
                if (_currentCharRate > 10 && _currentWindowSize < MAX_WINDOW_SIZE)
                {
                    _currentWindowSize++;
                }
                else if (_currentCharRate < 5 && _currentWindowSize > MIN_WINDOW_SIZE)
                {
                    _currentWindowSize--;
                }
            }

            // 维护滑动窗口
            while (_speechRateWindow.Count > _currentWindowSize)
            {
                _speechRateWindow.Dequeue();
            }

            // 计算当前速率
            if (_speechRateWindow.Count >= 2)
            {
                var oldest = _speechRateWindow.Peek();
                var totalWords = _speechRateWindow.Sum(x => x.wordCount);
                var totalChars = _speechRateWindow.Sum(x => x.charCount);
                var timeSpan = (now - oldest.timestamp).TotalSeconds;

                if (timeSpan > 0)
                {
                    // 使用指数移动平均来平滑速率变化
                    var newWordRate = totalWords / timeSpan;
                    var newCharRate = totalChars / timeSpan;
                    
                    const double alpha = 0.3; // 平滑因子
                    _currentSpeechRate = _currentSpeechRate * (1 - alpha) + newWordRate * alpha;
                    _currentCharRate = _currentCharRate * (1 - alpha) + newCharRate * alpha;
                }
            }
        }

        public override List<string> SplitIntoCompleteSentences(string text)
        {
            UpdateSpeechRate(text);
            return base.SplitIntoCompleteSentences(text);
        }

        public async Task<bool> HasNaturalPauseAsync(string text)
        {
            return await Task.Run(() => HasNaturalPause(text));
        }

        public async Task<int> FindLastNaturalPauseAsync(string text)
        {
            return await Task.Run(() => FindLastNaturalPause(text));
        }
    }
}
