using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher instance;
    private readonly Queue<Action> queue = new Queue<Action>();

    public static UnityMainThreadDispatcher Instance
    {
        get
        {
            if (instance == null)
            {
                var go = new GameObject("MainThreadDispatcher");
                instance = go.AddComponent<UnityMainThreadDispatcher>();
                DontDestroyOnLoad(go);
            }
            return instance;
        }
    }

    public static void Enqueue(Action action)
    {
        if (action == null)
            return;

        UnityMainThreadDispatcher dispatcher = Instance;
        lock (dispatcher.queue)
            dispatcher.queue.Enqueue(action);
    }
    
    private void Update()
    {
        while (true)
        {
            Action action;

            lock (queue)
            {
                if (queue.Count == 0)
                    return;

                action = queue.Dequeue();
            }

            action.Invoke();
        }
    }
}
