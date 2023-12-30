using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Timers;
using System.Windows.Threading;
using VRage;

namespace Advanced_PB_Limiter.Utils
{
    public class FastConcurrentObservableCollection<T> : INotifyCollectionChanged
    {
        private ConcurrentQueue<T> _items = new ();
        private List<NotifyCollectionChangedEventArgs> _pendingChanges = new ();
        private readonly FastResourceLock _resourceLock = new ();
        private readonly Dispatcher _uiDispatcher;
        private readonly Timer uiUpdateTimer = new Timer(2000);

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public FastConcurrentObservableCollection(Dispatcher uiDispatcher)
        {
            _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
            uiUpdateTimer.Elapsed += (sender, args) => UpdateUI();
            uiUpdateTimer.Start();
        }

        public void Add(T item)
        {
            _items.Enqueue(item);
            _resourceLock.AcquireExclusive();
            try
            {
                _pendingChanges.Add(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item));
            }
            finally
            {
                _resourceLock.ReleaseExclusive();
            }
        }

        public bool Remove(T item)
        {
            bool removed = false;
            _resourceLock.AcquireExclusive();
            try
            {
                // Convert the ConcurrentQueue to a List for manipulation
                List<T> tempList = new List<T>(_items);

                // Remove the item
                removed = tempList.Remove(item);

                if (removed)
                {
                    // Recreate the ConcurrentQueue from the modified list
                    _items = new ConcurrentQueue<T>(tempList);

                    // Record the change for notification
                    _pendingChanges.Add(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item));
                }
            }
            finally
            {
                _resourceLock.ReleaseExclusive();
            }
            return removed;
        }

        public void Clear()
        {
            _resourceLock.AcquireExclusive();
            try
            {
                _items = new ConcurrentQueue<T>();
                _pendingChanges.Add(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
            finally
            {
                _resourceLock.ReleaseExclusive();
            }
        }

        public void UpdateUI()
        {
            List<NotifyCollectionChangedEventArgs> changesToRaise;
            _resourceLock.AcquireExclusive();
            try
            {
                changesToRaise = new List<NotifyCollectionChangedEventArgs>(_pendingChanges);
                _pendingChanges.Clear();
            }
            finally
            {
                _resourceLock.ReleaseExclusive();
            }

            if (_uiDispatcher.CheckAccess())
            {
                RaiseEvents(changesToRaise);
            }
            else
            {
                _uiDispatcher.Invoke(() => RaiseEvents(changesToRaise));
            }
        }
        
        private void RaiseEvents(List<NotifyCollectionChangedEventArgs> changes)
        {
            foreach (NotifyCollectionChangedEventArgs? change in changes)
            {
                CollectionChanged?.Invoke(this, change);
            }
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(_items.ToArray()); // Snapshot for thread safety
        }

        public struct Enumerator : IEnumerator<T>
        {
            private readonly T[] _snapshot;
            private int _index;

            public Enumerator(T[] snapshot)
            {
                _snapshot = snapshot;
                _index = -1;
            }

            public T Current => _snapshot[_index];

            object System.Collections.IEnumerator.Current => Current;

            public void Dispose() { } // Nothing to dispose here

            public bool MoveNext()
            {
                _index++;
                return _index < _snapshot.Length;
            }

            public void Reset()
            {
                _index = -1;
            }
        }
    }
}