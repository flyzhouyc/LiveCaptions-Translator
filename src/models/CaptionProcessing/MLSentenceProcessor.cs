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
        private const int SPEECH_RATE_WINDOW_SIZE = 10;
        private readonly Queue<(DateTime timestamp, int wordCount)> _speechRateWindow = new();
        private static readonly TimeSpan MIN_WAIT_TIME = TimeSpan.FromSeconds(0.5);
        private static readonly TimeSpan MAX_WAIT_TIME = TimeSpan.FromSeconds(3);
        private double _currentSpeechRate = 0; // 词/秒

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
            if (_currentSpeechRate <= 0) return MAX_WAIT_TIME;

            // 根据语速动态调整等待时间
            // 语速快时等待时间短，语速慢时等待时间长
            double normalizedRate = Math.Min(_currentSpeechRate / 3.0, 1.0); // 3词/秒作为基准
            double waitTime = MAX_WAIT_TIME.TotalSeconds - 
                (MAX_WAIT_TIME.TotalSeconds - MIN_WAIT_TIME.TotalSeconds) * normalizedRate;
            
            return TimeSpan.FromSeconds(waitTime);
        }

        public void UpdateSpeechRate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var wordCount = text.Split(new[] { ' ', '\t', '\n', '\r' }, 
                StringSplitOptions.RemoveEmptyEntries).Length;
            var now = DateTime.Now;

            _speechRateWindow.Enqueue((now, wordCount));

            // 维护滑动窗口
            while (_speechRateWindow.Count > SPEECH_RATE_WINDOW_SIZE)
            {
                _speechRateWindow.Dequeue();
            }

            // 计算当前语速
            if (_speechRateWindow.Count >= 2)
            {
                var oldest = _speechRateWindow.Peek();
                var totalWords = _speechRateWindow.Sum(x => x.wordCount);
                var timeSpan = (now - oldest.timestamp).TotalSeconds;
                _currentSpeechRate = totalWords / timeSpan;
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
