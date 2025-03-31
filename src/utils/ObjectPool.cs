using System;
using System.Collections.Concurrent;
using System.Threading;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// 通用对象池实现，用于减少频繁创建和销毁对象导致的GC压力
    /// </summary>
    /// <typeparam name="T">池化的对象类型</typeparam>
    public class ObjectPool<T> where T : class
    {
        // 使用ConcurrentBag提供高性能的线程安全集合
        private readonly ConcurrentBag<T> _objects;
        // 对象工厂函数
        private readonly Func<T> _objectGenerator;
        // 对象重置函数
        private readonly Action<T> _objectReset;
        // 最大池大小
        private readonly int _maxSize;
        // 当前池大小计数器
        private int _count;

        /// <summary>
        /// 初始化对象池
        /// </summary>
        /// <param name="objectGenerator">创建新对象的工厂函数</param>
        /// <param name="objectReset">重置对象状态的函数</param>
        /// <param name="maxSize">池的最大大小，超过此大小的对象将被丢弃</param>
        public ObjectPool(Func<T> objectGenerator, Action<T> objectReset = null, int maxSize = 50)
        {
            _objects = new ConcurrentBag<T>();
            _objectGenerator = objectGenerator ?? throw new ArgumentNullException(nameof(objectGenerator));
            _objectReset = objectReset;
            _maxSize = maxSize;
            _count = 0;
        }

        /// <summary>
        /// 从池中获取对象，如果池为空则创建新对象
        /// </summary>
        public T Get()
        {
            if (_objects.TryTake(out T item))
            {
                return item;
            }

            Interlocked.Increment(ref _count);
            return _objectGenerator();
        }

        /// <summary>
        /// 将对象返回到池中
        /// </summary>
        /// <param name="item">要返回的对象</param>
        public void Return(T item)
        {
            if (item == null) return;

            // 重置对象状态
            _objectReset?.Invoke(item);

            // 如果池已满，直接丢弃对象
            if (Interlocked.CompareExchange(ref _count, _maxSize, _maxSize) >= _maxSize)
            {
                Interlocked.Decrement(ref _count);
                return;
            }

            _objects.Add(item);
        }

        /// <summary>
        /// 清空对象池
        /// </summary>
        public void Clear()
        {
            while (_objects.TryTake(out _)) 
            {
                Interlocked.Decrement(ref _count);
            }
        }

        /// <summary>
        /// 获取当前池中的对象数量
        /// </summary>
        public int Count => _objects.Count;
    }
}