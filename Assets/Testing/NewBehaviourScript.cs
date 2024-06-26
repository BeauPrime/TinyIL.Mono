using System;
using System.Collections;
using System.Collections.Generic;
using TinyIL;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.UIElements.Experimental;

#if UNITY_EDITOR
using UnityEditor.Build.Reporting;
using UnityEditor.Build;
using UnityEditor;
using UnityEditorInternal;
#endif // UNITY_EDITOR

public class NewBehaviourScript : MonoBehaviour
{
    // Start is called before the first frame update
    void Awake()
    {
        if (IsTinyILCompiled()) {
            Debug.Log("TinyIL compilation successful!");
        } else { 
            Debug.LogError("TinyIL compilation unsuccessful");
        } 

        var loadState = GetLoadingState(gameObject.scene);
        Debug.Log(loadState);
        Debug.Log(loadState.GetType().FullName);
        Debug.Log(GetGuid(gameObject.scene)); 
        Debug.Log(ToInt(loadState));  

        Debug.Log(FnvHash(gameObject.name).ToString("X8"));
        CallITest();
        Debug.Log(FnvHash("Aqualab"));
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

    private struct NestedTest
    {
        [IntrinsicIL("ldarg.0; ret")]
        static public int CheckNested(int a)
        {
            throw new NotImplementedException();
        }
    }

    [IntrinsicIL("ldc.i4.1; ret;")]
    static private bool IsTinyILCompiled() {
        return false;
    }

    [ExternalIL("NewBehaviour:FNV_HASH")]
    static private unsafe uint FnvHash32(char* ptr, int len) {
        if (len == 0) {
            return 0;
        }

        uint hashVal = 2166136261;
        while(len-- > 0) {
            hashVal = (hashVal ^ *ptr++) * 16777619;
        }
        return hashVal;
    }

    [IntrinsicIL("ldstr Aqualab; ldftn [declaringType]::FnvHash(string); calli uint32(string); box int32; call UnityEngine.Debug::Log(object); ret;")]
    static private void CallITest() {
        throw new NotImplementedException();
    }

    static private unsafe uint FnvHash(string str) {
        fixed(char* ptr = str) {
            return FnvHash32(ptr, str.Length);
        } 
    }

    [IntrinsicIL("ldarga.s scene; call UnityEngine.SceneManagement.Scene::get_loadingState(); conv.i4; ret;")]
    static private SceneLoadState GetLoadingState(Scene scene) {  
        throw new NotImplementedException();   
    }

    [IntrinsicIL("ldarg.0; call UnityEngine.UI.Selectable::get_currentSelectionState(); conv.i4; ret;")]
    static private int GetSelectionState(Selectable selectable) {
        throw new NotImplementedException();
    }

    [IntrinsicIL("ldarga.s scene; call UnityEngine.SceneManagement.Scene::get_guid(); ret;")]
    static private string GetGuid(Scene scene) {
        throw new NotImplementedException();
    }

    //[IntrinsicIL("ldarg.0; call System.Collections.HashHelpers::ExpandPrime(int32); ret;")]
    //static private int GetNextPrime(int val) {
    //    throw new NotImplementedException();
    //}

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

#if UNITY_EDITOR

    private class AssertTinyILHasRunBuildStep : IPreprocessBuildWithReport {
        public int callbackOrder { get { return -10000; } }

        public void OnPreprocessBuild(BuildReport report) {
            if (!IsTinyILCompiled()) {
                Debug.LogError("TinyIL compilation not ready!");
                throw new BuildFailedException("TinyIL processing didn't run");
            }
        }
    }

#endif // UNITY_EDITOR
}
