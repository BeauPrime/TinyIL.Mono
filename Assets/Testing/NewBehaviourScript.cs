using System;
using System.Collections;
using System.Collections.Generic;
using TinyIL;
using UnityEngine;
using UnityEngine.SceneManagement;

public class NewBehaviourScript : MonoBehaviour
{
    // Start is called before the first frame update
    void Awake()
    {
        var loadState = GetLoadingState(gameObject.scene);
        Debug.Log(loadState);
        Debug.Log(loadState.GetType().FullName);
        Debug.Log(GetGuid(gameObject.scene));
        Debug.Log(ToInt(loadState));
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    private enum SceneLoadState {
        NotLoaded,
        Loading,
        Loaded,
        Unloading
    }

    [IntrinsicIL("ldarga.s scene; call UnityEngine.SceneManagement.Scene::get_loadingState(); conv.i4; ret;")]
    static private SceneLoadState GetLoadingState(Scene scene) {  
        throw new NotImplementedException();  
    }

    [IntrinsicIL("ldarga.s scene; call UnityEngine.SceneManagement.Scene::get_guid(); ret;")]
    static private string GetGuid(Scene scene) {
        throw new NotImplementedException();
    }

    [IntrinsicIL("ldarg.0; conv.i4; ret")] // non-boxing conversion
    static public int ToInt<T>(T value) where T : struct, Enum {
        // boxing conversion
        return Convert.ToInt32(value);
    }

    [IntrinsicIL("ldarg.0; ldc.i4.0; ldarg.1; sizeof !!T; mul; unaligned. 1; initblk; ret")] // using initblk
    static public unsafe void ClearBytes<T>(T* start, int length) where T : unmanaged {
        // byte-by-byte clear
        byte* end = (byte*) (start + length);
        byte* ptr = (byte*) start;
        while (ptr < end) {
            *(ptr++) = 0;
        }
    }
}
