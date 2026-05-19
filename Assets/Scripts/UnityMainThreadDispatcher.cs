using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher instance;
    private Queue<Action> queue = new Queue<Action>();      // codes in the Action will be excuted later(after mainthread)

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
        Instance.queue.Enqueue(action);
    }
    
    void Update()
    {
        while(queue.Count > 0)
            queue.Dequeue()?.Invoke();
    }
}