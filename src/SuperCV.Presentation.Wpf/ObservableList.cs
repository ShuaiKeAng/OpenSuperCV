using System;
using System.Collections.Generic;

namespace SuperCV
{
    public class ObservableList<T> : List<T>
    {
        public event EventHandler? ListChanged;

        public void Changed()
        {
            OnListChanged();
        }
        
        public new void Add(T item)
        {
            base.Add(item);
            OnListChanged();
        }
        public void AddOff(T item)
        {
            base.Add(item);
            //OnListChanged();
        }
        public new void AddRange(IEnumerable<T> collection)
        {
            int previousCount = Count;
            base.AddRange(collection);
            if (Count != previousCount)
            {
                OnListChanged();
            }
        }

        public new void RemoveRange(int index, int count)
        {
            base.RemoveRange(index, count);
            if (count != 0)
            {
                OnListChanged();
            }
        }

        public new void Remove(T item)
        {
            if (base.Remove(item))
            {
                OnListChanged();
            }
        }
        public new void Insert(int i,T item)
        {
            base.Insert(i,item);
            OnListChanged();
        }
        public new void RemoveAt(int index)
        {
            base.RemoveAt(index);
            OnListChanged();
        }

        public new void Clear()
        {
            if (Count == 0)
            {
                return;
            }

            base.Clear();
            OnListChanged();
        }
        public void ClearOff()
        {
            base.Clear();
        }
        public new T this[int index]
        {
            get => base[index];
            set
            {
                base[index] = value;
                OnListChanged();
            }
        }

        protected virtual void OnListChanged()
        {
            ListChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
