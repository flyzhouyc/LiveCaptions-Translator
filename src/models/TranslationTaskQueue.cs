namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();

        // 存储任务队列
        private readonly List<TranslationTask> tasks;
        
        // 存储完成的翻译结果，保持最新的几个
        private readonly Queue<string> completedResults;
        
        // 最大保留的已完成翻译结果数量
        private const int MaxCompletedResults = 3;
        
        // 当前输出
        private string output;

        public string Output
        {
            get => output;
        }

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            completedResults = new Queue<string>();
            output = string.Empty;
        }

        public void Enqueue(Func<CancellationToken, Task<string>> worker)
        {
            try 
            {
                var cts = new CancellationTokenSource();
                // 创建任务但不立即执行
                var task = worker(cts.Token);
                var newTranslationTask = new TranslationTask(task, cts, DateTime.Now);
                
                lock (_lock)
                {
                    // 最多保留5个任务在队列中，避免资源浪费
                    const int maxQueueSize = 5;
                    while (tasks.Count >= maxQueueSize)
                    {
                        // 取消并移除最旧的任务
                        var oldestTask = tasks.OrderBy(t => t.CreationTime).First();
                        oldestTask.CTS.Cancel();
                        tasks.Remove(oldestTask);
                    }
                    
                    tasks.Add(newTranslationTask);
                }
                
                // 处理任务完成
                task.ContinueWith(t => 
                {
                    try 
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            OnTaskCompleted(newTranslationTask);
                        }
                        else if (t.IsFaulted && t.Exception != null)
                        {
                            // 记录异常并提供错误信息作为输出
                            string errorMsg = $"[翻译失败] {t.Exception.InnerException?.Message ?? t.Exception.Message}";
                            Console.WriteLine($"翻译任务异常: {errorMsg}");
                            
                            lock (_lock)
                            {
                                completedResults.Enqueue(errorMsg);
                                while (completedResults.Count > MaxCompletedResults)
                                {
                                    completedResults.Dequeue();
                                }
                                output = errorMsg;
                                tasks.Remove(newTranslationTask);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"处理翻译任务结果时发生异常: {ex.Message}");
                    }
                });
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"创建翻译任务时发生异常: {ex.Message}");
            }
        }

        private void OnTaskCompleted(TranslationTask translationTask)
        {
            try 
            {
                lock (_lock)
                {
                    if (translationTask.Task.IsCanceled || translationTask.Task.IsFaulted)
                        return;

                    // 获取任务结果
                    string result = translationTask.Task.Result;
                    Console.WriteLine($"翻译任务完成，结果: {result}");
                    
                    // 只有结果非空才处理
                    if (!string.IsNullOrEmpty(result))
                    {
                        // 将结果添加到完成队列
                        completedResults.Enqueue(result);
                        
                        // 保持队列在最大容量范围内
                        while (completedResults.Count > MaxCompletedResults)
                        {
                            completedResults.Dequeue();
                        }
                        
                        // 更新当前输出为最新的结果
                        output = result;
                    }
                    
                    // 从任务列表中移除当前任务
                    tasks.Remove(translationTask);
                    
                    // 找出所有已经完成的任务并移除
                    var completedTasks = tasks.Where(t => t.Task.IsCompleted).ToList();
                    foreach (var task in completedTasks)
                    {
                        tasks.Remove(task);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理完成任务时发生异常: {ex.Message}");
            }
        }
        
        // 在需要时可以获取备选的翻译结果（如果最新的翻译结果不可用）
        public string GetLatestAvailableResult()
        {
            lock (_lock)
            {
                // 优先返回当前输出
                if (!string.IsNullOrEmpty(output))
                {
                    return output;
                }
                
                // 如果当前输出为空，则返回队列中最新的结果
                return completedResults.Count > 0 ? completedResults.Last() : string.Empty;
            }
        }
    }

    public class TranslationTask
    {
        public Task<string> Task { get; }
        public CancellationTokenSource CTS { get; }
        public DateTime CreationTime { get; }

        public TranslationTask(Task<string> task, CancellationTokenSource cts, DateTime creationTime)
        {
            Task = task;
            CTS = cts;
            CreationTime = creationTime;
        }
    }
}