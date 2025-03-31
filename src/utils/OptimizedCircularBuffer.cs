using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// 优化的循环缓冲区，避免频繁求模运算，提高性能
    /// </summary>
    public class OptimizedCircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _start;
        private int _count;
        private readonly int _capacityMask;
        private readonly ReaderWriterLockSlim _bufferLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        
        public int Count => _count;
        public int Capacity => _buffer.Length;
        
        /// <summary>
        /// 创建一个新的优化循环缓冲区
        /// </summary>
        /// <param name="capacity">缓冲区容量，会被调整为2的幂</param>
        public OptimizedCircularBuffer(int capacity)
        {
            // 确保容量是2的幂，这样可以用位操作代替求模
            int powerOfTwo = 1;
            while (powerOfTwo < capacity)
                powerOfTwo <<= 1;
                
            _buffer = new T[powerOfTwo];
            _capacityMask = powerOfTwo - 1; // 例如：容量为8，掩码为7 (0b111)
            _start = 0;
            _count = 0;
        }
        
        /// <summary>
        /// 添加元素到缓冲区
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            _bufferLock.EnterWriteLock();
            try
            {
                if (_count == _buffer.Length)
                {
                    // 缓冲区已满，覆盖最早的项
                    _buffer[_start] = item;
                    _start = (_start + 1) & _capacityMask; // 使用位操作代替求模
                }
                else
                {
                    // 缓冲区未满，添加到末尾
                    _buffer[(_start + _count) & _capacityMask] = item;
                    _count++;
                }
            }
            finally
            {
                _bufferLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// 批量添加元素到缓冲区
        /// </summary>
        public void AddRange(IEnumerable<T> items)
        {
            if (items == null)
                return;
                
            _bufferLock.EnterWriteLock();
            try
            {
                foreach (var item in items)
                {
                    if (_count == _buffer.Length)
                    {
                        // 缓冲区已满，覆盖最早的项
                        _buffer[_start] = item;
                        _start = (_start + 1) & _capacityMask;
                    }
                    else
                    {
                        // 缓冲区未满，添加到末尾
                        _buffer[(_start + _count) & _capacityMask] = item;
                        _count++;
                    }
                }
            }
            finally
            {
                _bufferLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// 获取缓冲区中的所有元素
        /// </summary>
        public IEnumerable<T> GetItems()
        {
            _bufferLock.EnterReadLock();
            try
            {
                for (int i = 0; i < _count; i++)
                {
                    yield return _buffer[(_start + i) & _capacityMask];
                }
            }
            finally
            {
                _bufferLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// 获取缓冲区中的所有元素为数组
        /// </summary>
        public T[] ToArray()
        {
            _bufferLock.EnterReadLock();
            try
            {
                T[] result = new T[_count];
                for (int i = 0; i < _count; i++)
                {
                    result[i] = _buffer[(_start + i) & _capacityMask];
                }
                return result;
            }
            finally
            {
                _bufferLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// 获取指定索引的元素
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetItem(int index)
        {
            _bufferLock.EnterReadLock();
            try
            {
                if (index < 0 || index >= _count)
                    throw new IndexOutOfRangeException();
                    
                return _buffer[(_start + index) & _capacityMask];
            }
            finally
            {
                _bufferLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// 清空缓冲区
        /// </summary>
        public void Clear()
        {
            _bufferLock.EnterWriteLock();
            try
            {
                _start = 0;
                _count = 0;
                Array.Clear(_buffer, 0, _buffer.Length);
            }
            finally
            {
                _bufferLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// 重置缓冲区，可选择保留一个元素
        /// </summary>
        public void Reset(T itemToKeep = default)
        {
            _bufferLock.EnterWriteLock();
            try
            {
                Clear();
                if (itemToKeep != null && !itemToKeep.Equals(default(T)))
                {
                    Add(itemToKeep);
                }
            }
            finally
            {
                _bufferLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// 移除并返回最早的元素
        /// </summary>
        public T Dequeue()
        {
            _bufferLock.EnterWriteLock();
            try
            {
                if (_count == 0)
                    throw new InvalidOperationException("缓冲区为空");
                    
                T item = _buffer[_start];
                _buffer[_start] = default; // 清除引用
                _start = (_start + 1) & _capacityMask;
                _count--;
                
                return item;
            }
            finally
            {
                _bufferLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// 尝试移除并返回最早的元素
        /// </summary>
        public bool TryDequeue(out T item)
        {
            _bufferLock.EnterWriteLock();
            try
            {
                if (_count == 0)
                {
                    item = default;
                    return false;
                }
                
                item = _buffer[_start];
                _buffer[_start] = default; // 清除引用
                _start = (_start + 1) & _capacityMask;
                _count--;
                
                return true;
            }
            finally
            {
                _bufferLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// 查看最早的元素但不移除
        /// </summary>
        public T Peek()
        {
            _bufferLock.EnterReadLock();
            try
            {
                if (_count == 0)
                    throw new InvalidOperationException("缓冲区为空");
                    
                return _buffer[_start];
            }
            finally
            {
                _bufferLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// 尝试查看最早的元素但不移除
        /// </summary>
        public bool TryPeek(out T item)
        {
            _bufferLock.EnterReadLock();
            try
            {
                if (_count == 0)
                {
                    item = default;
                    return false;
                }
                
                item = _buffer[_start];
                return true;
            }
            finally
            {
                _bufferLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// 检查缓冲区是否包含指定元素
        /// </summary>
        public bool Contains(T item)
        {
            _bufferLock.EnterReadLock();
            try
            {
                for (int i = 0; i < _count; i++)
                {
                    T current = _buffer[(_start + i) & _capacityMask];
                    if (EqualityComparer<T>.Default.Equals(current, item))
                        return true;
                }
                
                return false;
            }
            finally
            {
                _bufferLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// 将元素复制到数组
        /// </summary>
        public void CopyTo(T[] array, int arrayIndex)
        {
            _bufferLock.EnterReadLock();
            try
            {
                if (array == null)
                    throw new ArgumentNullException(nameof(array));
                    
                if (arrayIndex < 0)
                    throw new ArgumentOutOfRangeException(nameof(arrayIndex));
                    
                if (array.Length - arrayIndex < _count)
                    throw new ArgumentException("目标数组太小");
                    
                for (int i = 0; i < _count; i++)
                {
                    array[arrayIndex + i] = _buffer[(_start + i) & _capacityMask];
                }
            }
            finally
            {
                _bufferLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _bufferLock.Dispose();
        }
    }
}