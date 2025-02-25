// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Linq;


namespace Microsoft.ML.OnnxRuntime
{


    /// <summary>
    /// Represents an Inference Session on an ONNX Model
    /// </summary>
    public class InferenceSession : IDisposable
    {
        protected IntPtr _nativeHandle;
        protected Dictionary<string, NodeMetadata> _inputMetadata, _outputMetadata;
        private SessionOptions _builtInSessionOptions = null;
        private RunOptions _builtInRunOptions = null;


        #region Public API

        /// <summary>
        /// Constructs an InferenceSession from a model file
        /// </summary>
        /// <param name="modelPath"></param>
        public InferenceSession(string modelPath)
        {
            _builtInSessionOptions = new SessionOptions(); // need to be disposed
            Init(modelPath, _builtInSessionOptions);
        }


        /// <summary>
        /// Constructs an InferenceSession from a model file, with some additional session options
        /// </summary>
        /// <param name="modelPath"></param>
        /// <param name="options"></param>
        public InferenceSession(string modelPath, SessionOptions options)
        {
            Init(modelPath, options);
        }


        /// <summary>
        /// Meta data regarding the input nodes, keyed by input names
        /// </summary>
        public IReadOnlyDictionary<string, NodeMetadata> InputMetadata
        {
            get
            {
                return _inputMetadata;
            }
        }

        /// <summary>
        /// Metadata regarding the output nodes, keyed by output names
        /// </summary>
        public IReadOnlyDictionary<string, NodeMetadata> OutputMetadata
        {
            get
            {
                return _outputMetadata;
            }
        }


        /// <summary>
        /// Runs the loaded model for the given inputs, and fetches all the outputs.
        /// </summary>
        /// <param name="inputs"></param>
        /// <returns>Output Tensors in a Collection of NamedOnnxValue. User must dispose the output.</returns>
        public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
        {
            string[] outputNames = new string[_outputMetadata.Count];
            _outputMetadata.Keys.CopyTo(outputNames, 0);
            return Run(inputs, outputNames);
        }

        /// <summary>
        /// Runs the loaded model for the given inputs, and fetches the outputs specified in <paramref name="outputNames"/>.
        /// </summary>
        /// <param name="inputs"></param>
        /// <param name="outputNames"></param>
        /// <returns>Output Tensors in a Collection of NamedOnnxValue. User must dispose the output.</returns>
        public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs, IReadOnlyCollection<string> outputNames)
        {
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result = null;
            result = Run(inputs, outputNames, _builtInRunOptions);
            return result;
        }

        /// <summary>
        /// Runs the loaded model for the given inputs, and fetches the specified outputs in <paramref name="outputNames". Uses the given RunOptions for this run./>.
        /// </summary>
        /// <param name="inputs"></param>
        /// <param name="outputNames"></param>
        /// <param name="options"></param>
        /// <returns>Output Tensors in a Collection of NamedOnnxValue. User must dispose the output.</returns>
        public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs, IReadOnlyCollection<string> outputNames, RunOptions options)
        {
            var inputNames = new string[inputs.Count];
            var inputTensors = new IntPtr[inputs.Count];
            var pinnedBufferHandles = new System.Buffers.MemoryHandle[inputs.Count];

            int inputIndex = 0;
            foreach (var input in inputs)
            {
                inputNames[inputIndex] = input.Name;

                // create Tensor from the input if feasible, else throw notsupported exception for now
                input.ToNativeOnnxValue(out inputTensors[inputIndex], out pinnedBufferHandles[inputIndex]);

                inputIndex++;
            }

            string[] outputNamesArray = outputNames.ToArray();
            IntPtr[] outputValueArray = new IntPtr[outputNames.Count];

            IntPtr status = NativeMethods.OrtRun(
                                                this._nativeHandle,
                                                IntPtr.Zero,  // TODO: use Run options when Run options creation API is available
                                                              // Passing null uses the default run options in the C-api
                                                inputNames,
                                                inputTensors,
                                                (UIntPtr)(inputTensors.Length),
                                                outputNamesArray,
                                                (UIntPtr)outputNames.Count,
                                                outputValueArray /* An array of output value pointers. Array must be allocated by the caller */
                                                );

            try
            {
                NativeApiStatus.VerifySuccess(status);
                var result = new DisposableList<DisposableNamedOnnxValue>();
                for (uint i = 0; i < outputValueArray.Length; i++)
                {
                    result.Add(DisposableNamedOnnxValue.CreateFromOnnxValue(outputNamesArray[i], outputValueArray[i]));
                }

                return result;
            }
            catch (OnnxRuntimeException e)
            {
                //clean up the individual output tensors if it is not null;
                for (uint i = 0; i < outputValueArray.Length; i++)
                {
                    if (outputValueArray[i] != IntPtr.Zero)
                    {
                        NativeMethods.OrtReleaseValue(outputValueArray[i]);
                    }
                }
                throw e;
            }
            finally
            {
                // always unpin the input buffers, and delete the native Onnx value objects
                for (int i = 0; i < inputs.Count; i++)
                {
                    NativeMethods.OrtReleaseValue(inputTensors[i]); // this should not release the buffer, but should delete the native tensor object
                    pinnedBufferHandles[i].Dispose();
                }
            }

        }

        //TODO: kept internal until implemented
        internal ModelMetadata ModelMetadata
        {
            get
            {
                return new ModelMetadata(); //TODO: implement
            }
        }

        #endregion

        #region private methods

        protected void Init(string modelPath, SessionOptions options)
        {
            var envHandle = OnnxRuntime.Handle;

            _nativeHandle = IntPtr.Zero;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    NativeApiStatus.VerifySuccess(NativeMethods.OrtCreateSession(envHandle, System.Text.Encoding.Unicode.GetBytes(modelPath), options.Handle, out _nativeHandle));
                else
                    NativeApiStatus.VerifySuccess(NativeMethods.OrtCreateSession(envHandle, System.Text.Encoding.UTF8.GetBytes(modelPath), options.Handle, out _nativeHandle));

                // Initialize input/output metadata
                _inputMetadata = new Dictionary<string, NodeMetadata>();
                _outputMetadata = new Dictionary<string, NodeMetadata>();

                // get input count
                UIntPtr inputCount = UIntPtr.Zero;
                NativeApiStatus.VerifySuccess(NativeMethods.OrtSessionGetInputCount(_nativeHandle, out inputCount));

                // get all the output names
                for (ulong i = 0; i < (ulong)inputCount; i++)
                {
                    var iname = GetInputName(i);
                    _inputMetadata[iname] = GetInputMetadata(i);
                }
                // get output count
                UIntPtr outputCount = UIntPtr.Zero;
                NativeApiStatus.VerifySuccess(NativeMethods.OrtSessionGetOutputCount(_nativeHandle, out outputCount));

                // get all the output names
                for (ulong i = 0; i < (ulong)outputCount; i++)
                {
                    _outputMetadata[GetOutputName(i)] = GetOutputMetadata(i);
                }

            }
            catch (OnnxRuntimeException e)
            {
                if (_nativeHandle != IntPtr.Zero)
                {
                    NativeMethods.OrtReleaseSession(_nativeHandle);
                    _nativeHandle = IntPtr.Zero;
                }
                throw e;
            }

            _builtInRunOptions = new RunOptions();  // create a default built-in run option, and avoid creating a new one every run() call
        }


        private string GetOutputName(ulong index)
        {
            IntPtr nameHandle = IntPtr.Zero;
            string str = null;

            IntPtr status = NativeMethods.OrtSessionGetOutputName(
                                                _nativeHandle,
                                                (UIntPtr)index,
                                                NativeMemoryAllocator.DefaultInstance.Handle,
                                                out nameHandle);
            try
            {
                NativeApiStatus.VerifySuccess(status);
                str = Marshal.PtrToStringAnsi(nameHandle); //assumes charset = ANSI
            }
            finally
            {
                if (nameHandle != IntPtr.Zero)
                {
                    NativeMemoryAllocator.DefaultInstance.FreeMemory(nameHandle);
                }
            }

            return str;
        }

        private string GetInputName(ulong index)
        {
            IntPtr nameHandle = IntPtr.Zero;
            string str = null;

            IntPtr status = NativeMethods.OrtSessionGetInputName(
                                                _nativeHandle,
                                                (UIntPtr)index,
                                                NativeMemoryAllocator.DefaultInstance.Handle,
                                                out nameHandle);
            try
            {

                NativeApiStatus.VerifySuccess(status);
                str = Marshal.PtrToStringAnsi(nameHandle); //assumes charset = ANSI
            }
            finally
            {
                if (nameHandle != IntPtr.Zero)
                {
                    NativeMemoryAllocator.DefaultInstance.FreeMemory(nameHandle);
                }
            }
            return str;
        }


        private NodeMetadata GetInputMetadata(ulong index)
        {
            IntPtr typeInfo = IntPtr.Zero;
            try
            {
                NativeApiStatus.VerifySuccess(NativeMethods.OrtSessionGetInputTypeInfo(_nativeHandle, (UIntPtr)index, out typeInfo));
                return GetMetadataFromTypeInfo(typeInfo);
            }
            finally
            {
                if (typeInfo != IntPtr.Zero)
                {
                    NativeMethods.OrtReleaseTypeInfo(typeInfo);
                }
            }
        }

        private NodeMetadata GetOutputMetadata(ulong index)
        {
            IntPtr typeInfo = IntPtr.Zero;
            try
            {
                NativeApiStatus.VerifySuccess(NativeMethods.OrtSessionGetOutputTypeInfo(_nativeHandle, (UIntPtr)index, out typeInfo));
                return GetMetadataFromTypeInfo(typeInfo);
            }
            finally
            {
                if (typeInfo != IntPtr.Zero)
                {
                    NativeMethods.OrtReleaseTypeInfo(typeInfo);
                }
            }
        }

        internal static NodeMetadata GetMetadataFromTypeInfo(IntPtr typeInfo)
        {
            OnnxValueType valueType;
            unsafe
            {
                NativeApiStatus.VerifySuccess(NativeMethods.OrtGetOnnxTypeFromTypeInfo(typeInfo, new IntPtr(&valueType)));
            }
            if (valueType != OnnxValueType.ONNX_TYPE_TENSOR && valueType != OnnxValueType.ONNX_TYPE_SPARSETENSOR)
            {
                return new NodeMetadata(valueType, new int[] { }, typeof(NamedOnnxValue));
            }

            IntPtr tensorInfo;
            NativeApiStatus.VerifySuccess(NativeMethods.OrtCastTypeInfoToTensorInfo(typeInfo, out tensorInfo)); //(IntPtr)(int)(uint)
            // Convert the newly introduced OrtTypeInfo* to the older OrtTypeAndShapeInfo*

            if (tensorInfo == IntPtr.Zero)
                return null;

            TensorElementType type;
            unsafe
            {
                NativeApiStatus.VerifySuccess(NativeMethods.OrtGetTensorElementType(tensorInfo, new IntPtr(&type)));
            }
            Type dotnetType = null;
            int width = 0;
            TensorElementTypeConverter.GetTypeAndWidth(type, out dotnetType, out width);
            UIntPtr numDimensions;
            NativeApiStatus.VerifySuccess(NativeMethods.OrtGetDimensionsCount(tensorInfo, out numDimensions));
            long[] dimensions = new long[(int)numDimensions];
            NativeMethods.OrtGetDimensions(tensorInfo, dimensions, numDimensions);
            int[] intDimensions = new int[(int)numDimensions];
            for (var i = 0; i < (long)numDimensions; i++)
            {
                intDimensions[i] = (int)dimensions[i];
            }
            return new NodeMetadata(valueType, intDimensions, dotnetType);
        }

        #endregion

        #region destructors disposers


        ~InferenceSession()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // cleanup managed resources
                if (_builtInSessionOptions != null)
                {
                    _builtInSessionOptions.Dispose();
                }

                if (_builtInRunOptions != null)
                {
                    _builtInRunOptions.Dispose();
                }
            }

            // cleanup unmanaged resources
            if (_nativeHandle != IntPtr.Zero)
            {
                NativeMethods.OrtReleaseSession(_nativeHandle);
            }
        }

        #endregion

    }


    /// <summary>
    /// Resembles type and shape information of session-graph nodes, used for communicating the shape/type of input/output nodes
    /// </summary>
    public class NodeMetadata
    {
        private OnnxValueType _onnxValueType;
        private int[] _dimensions;
        private Type _type;

        internal NodeMetadata(OnnxValueType onnxValueType, int[] dimensions, Type type)
        {
            _onnxValueType = onnxValueType;
            _dimensions = dimensions;
            _type = type;
        }

        public OnnxValueType OnnxValueType
        {
            get
            {
                return _onnxValueType;
            }
        }

        public int[] Dimensions
        {
            get
            {
                return _dimensions;
            }
        }
        public System.Type ElementType
        {
            get
            {
                return _type;
            }
        }

        public bool IsTensor
        {
            get
            {
                return true; // currently only Tensor nodes are supported
            }
        }
    }


    internal class ModelMetadata
    {
        //TODO: placeholder for Model metadata. Currently C-API does not expose this.
    }


}
