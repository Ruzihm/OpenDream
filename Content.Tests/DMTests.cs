using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenDreamRuntime;
using OpenDreamRuntime.Procs;
using OpenDreamRuntime.Rendering;
using OpenDreamShared.Dream;
using Robust.Shared.Asynchronous;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Tests
{
    [TestFixture]
    public sealed partial class DMTests : ContentUnitTest
    {
        public const string TestProject = "DMProject";
        public const string Map = "map.dmm";
        public const string InitializeEnvironment = "./environment.dme";

        private IDreamManager _dreamMan;
        private ITaskManager _taskManager;

        private enum DMTestType {
            CompileError,   // Should fail to compile
            RuntimeError,   // Should throw an exception at runtime
            NoError         // Should run without errors
        }

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            _taskManager = IoCManager.Resolve<ITaskManager>();
            _taskManager.Initialize();
            IComponentFactory componentFactory = IoCManager.Resolve<IComponentFactory>();
            componentFactory.RegisterClass<DMISpriteComponent>();
            componentFactory.GenerateNetIds();
            _dreamMan = IoCManager.Resolve<IDreamManager>();
            Compile(InitializeEnvironment);
            _dreamMan.Initialize(Path.ChangeExtension(InitializeEnvironment, "json"));
        }

        public string Compile(string sourceFile) {
            bool successfulCompile = DMCompiler.DMCompiler.Compile(new() {
                Files = new() { sourceFile }
            });

            return successfulCompile ? Path.ChangeExtension(sourceFile, "json") : null;
        }

        public void Cleanup(string compiledFile) {
            if (!File.Exists(compiledFile))
                return;

            File.Delete(compiledFile);
        }

        [Test, TestCaseSource(nameof(GetTests))]
        public void TestFiles(string sourceFile)
        {
            DMTestType testType = GetDMTestType(sourceFile);

            string compiledFile = Compile(sourceFile);
            if (testType == DMTestType.CompileError) {
                Assert.IsNull(compiledFile, $"Expected an error during DM compilation");
                return;
            }

            Assert.IsTrue(compiledFile is not null && File.Exists(compiledFile), $"Failed to compile DM source file");
            Assert.IsTrue(_dreamMan.LoadJson(compiledFile), $"Failed to load {compiledFile}");

            bool successfulRun = RunTest();
            if (testType == DMTestType.RuntimeError) {
                Assert.IsFalse(successfulRun, "A DM runtime was expected");
            } else {
                //TODO: This should use the runtime exception as the failure message
                Assert.IsTrue(successfulRun, "A DM runtime exception was thrown");
            }
            
            Cleanup(compiledFile);
        }

        private bool RunTest() {
            var prev = _dreamMan.DMExceptionCount;

            var result = DreamThread.Run(async (state) => {
                var world = _dreamMan.WorldInstance;

                if (_dreamMan.ObjectTree.GlobalProcs.TryGetValue("RunTest", out DreamProc proc)) {
                    return await state.Call(proc, null, null, new DreamProcArguments(null));
                } else {
                    Assert.Fail($"No global proc named RunTest");
                    return DreamValue.Null;
                }
            });

            return _dreamMan.DMExceptionCount == prev;
        }

        private static IEnumerable<string> GetTests()
        {
            Directory.SetCurrentDirectory(TestProject);

            foreach (string sourceFile in Directory.GetFiles("Tests", "*.dm", SearchOption.AllDirectories)) {
                yield return Path.GetFullPath(sourceFile);
            }
        }

        private static DMTestType GetDMTestType(string sourceFile) {
            using (StreamReader reader = new StreamReader(sourceFile)) {
                string firstLine = reader.ReadLine();

                if (firstLine.Contains("COMPILE ERROR", StringComparison.InvariantCulture))
                    return DMTestType.CompileError;
                else if (firstLine.Contains("RUNTIME ERROR", StringComparison.InvariantCulture))
                    return DMTestType.RuntimeError;
                else
                    return DMTestType.NoError;
            }
        }

        // TODO Move all tests below this line to the new auto-test system

        [Test]
        public void SyncReturn()
        {
            var prev = _dreamMan.DMExceptionCount;
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
            var result = DreamThread.Run(async (state) => {
                return new DreamValue(1337);
            });
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously

            Assert.That(result, Is.EqualTo(new DreamValue(1337)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        [Test]
        public void SyncReturnInAsync() {
            var prev = _dreamMan.DMExceptionCount;
            var sync_result = DreamThread.Run(async(state) => {
                state.Result = new DreamValue(420);
                await Task.Yield();
                return new DreamValue(1337);
            });

            Assert.That(sync_result, Is.EqualTo(new DreamValue(420)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        [Test]
        public void SyncCall() {
            var prev = _dreamMan.DMExceptionCount;
            var sync_result = DreamThread.Run(async(state) =>
            {
                var root = _dreamMan.WorldInstance;
                var proc = root.GetProc("sync_test");
                return await state.Call(proc, null, null, new DreamProcArguments(null));
            });

            Assert.That(sync_result, Is.EqualTo(new DreamValue(1992)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        [Test]
        public void Error() {
            var prev = _dreamMan.DMExceptionCount;

            var sync_result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("error_test");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(sync_result, Is.EqualTo(new DreamValue(1)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev + 1));
        }

        /*[Test, Timeout(10000)]
        public void AsyncCall() {

            var result = DreamValue.Null;

            DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("async_test");
                result = await state.Call(proc, world, null, new DreamProcArguments(null));
                state.Runtime.Shutdown = true;
                return DreamValue.Null;
            });

            runtime.Run();

            Assert.AreEqual(new DreamValue(1337), result);
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }*/

        [Test]
        public void CrashPropagation() {
            var prev = _dreamMan.DMExceptionCount;

            var sync_result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("crash_test");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(sync_result, Is.EqualTo(new DreamValue(1)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev + 1));
        }

        [Test]
        public void StackOverflow() {
            var prev = _dreamMan.DMExceptionCount;

            var sync_result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("stack_overflow_test");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(sync_result, Is.EqualTo(new DreamValue(1)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev + 1));
        }

        /*[Test, Timeout(10000)]
        public void WaitFor() {

            DreamValue result_1 = DreamValue.Null;
            DreamValue result_2 = DreamValue.Null;

            DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc_1 = world.GetProc("waitfor_1_a");
                result_1 = await state.Call(proc_1, world, null, new DreamProcArguments(null));

                var proc_2 = world.GetProc("waitfor_2_a");
                result_2 = await state.Call(proc_2, world, null, new DreamProcArguments(null));

                state.Runtime.Shutdown = true;
                return DreamValue.Null;
            });

            runtime.Run();

            Assert.AreEqual(new DreamValue(3), result_1);
            Assert.AreEqual(new DreamValue(2), result_2);
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }*/


        [Test]
        public void Default() {
            var prev = _dreamMan.DMExceptionCount;

            var result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("default_test");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            var obj = result.GetValueAsDreamObjectOfType(DreamPath.Datum);
            Assert.IsNotNull(obj);
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        [Test]
        public void CallTest()
        {
            var prev = _dreamMan.DMExceptionCount;
            var result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("call_test");
                var res= await state.Call(proc, world, null, new DreamProcArguments(null));
                return res;
            });

            //var result = DreamThread.Run(_dreamMan.WorldInstance.GetProc("call_test"), _dreamMan.WorldInstance, null,
            //    new DreamProcArguments(null));

            Assert.That(result, Is.EqualTo(new DreamValue(13)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        [Test]
        public void SuperCallTest() {
            var prev = _dreamMan.DMExceptionCount;
            var result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("super_call");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(result, Is.EqualTo(new DreamValue(127)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        [Test]
        public void ConditionalAccessTest() {
            var prev = _dreamMan.DMExceptionCount;

            var result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("conditional_access_test");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(result, Is.EqualTo(new DreamValue(1)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        [Test]
        public void DrConditionalAccessErrorTest()
        {
            var prev = _dreamMan.DMExceptionCount;

            var result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("conditional_access_test_error");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev + 1));
        }

        //TODO Failing test
        /*[Test]
        public void ConditionalCallTest() {
            var prev = _dreamMan.DMExceptionCount;

            var result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("conditional_call_test");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(result, Is.EqualTo(DreamValue.Null));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }*/

        [Test]
        public void ConditionalCallErrorTest()
        {
            var prev = _dreamMan.DMExceptionCount;

            var result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("conditional_call_test_error");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev + 1));
        }

        [Test]
        public void ConditionalMutateTest() {
            var prev = _dreamMan.DMExceptionCount;

            var result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("conditional_mutate");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(result, Is.EqualTo(new DreamValue(4)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        [Test]
        public void ClampValueTest() {
            var prev = _dreamMan.DMExceptionCount;

            var result = DreamThread.Run(async (state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("clamp_value");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
            Assert.That(result, Is.EqualTo(new DreamValue(1)));
        }

        [Test]
        public void Md5Test() {
            var prev = _dreamMan.DMExceptionCount;

            var result = DreamThread.Run(async (state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("md5_test");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
            Assert.That(result, Is.EqualTo(new DreamValue("c74318b61a3024520c466f828c043c79")));
        }

        [Test]
        public void ForLoopsTest()
        {
            var prev = _dreamMan.DMExceptionCount;
            var result = DreamThread.Run(async state =>
            {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("for_loops_test");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
            var resultList = result.GetValueAsDreamList();
            foreach(var value in resultList.GetValues())
            {
                Assert.That(value.GetValueAsInteger(), Is.EqualTo(3));
            }
        }

        [Test]
        public void MatrixOperationsTest()
        {
            var prev = _dreamMan.DMExceptionCount;
            DreamThread.Run(async state =>
            {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("matrix_operations_test");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        //TODO Failing test
        /*[TestCase("Hello, World!", ", ", -1, 1)]
        [TestCase("Hello, World!", ", ", 3, 3)]
        [TestCase("Hello, World!", ", ", 7, 0)]
        [TestCase("Hello, World!", ", ", 14, 0)]
        [TestCase("Hello, World!", ", ", 0, 0)]
        public void NonspantextTest(string haystack, string needles, int start, int valueResult)
        {
            var prev = _dreamMan.DMExceptionCount;

            var haystackDreamValue = new DreamValue(haystack);
            var needlesDreamValue = new DreamValue(needles);
            var startDreamValue = new DreamValue(start);
            var valueResultDreamValue = new DreamValue(valueResult);
            var listDreamValue = new List<DreamValue>() { haystackDreamValue, needlesDreamValue, startDreamValue };
            var result = DreamThread.Run(async state =>
            {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("nonspantext");
                return await state.Call(proc, world, null, new DreamProcArguments(listDreamValue));
            });
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
            Assert.That(result, Is.EqualTo(valueResultDreamValue));
        }*/

        [Test]
        public void AssertPass() {
            var prev = _dreamMan.DMExceptionCount;

            var sync_result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("assert_test_pass");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(sync_result, Is.EqualTo(new DreamValue(1)));
            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev));
        }

        [Test]
        public void AssertFail() {
            var prev = _dreamMan.DMExceptionCount;

            var sync_result = DreamThread.Run(async(state) => {
                var world = _dreamMan.WorldInstance;
                var proc = world.GetProc("assert_test_fail");
                return await state.Call(proc, world, null, new DreamProcArguments(null));
            });

            Assert.That(_dreamMan.DMExceptionCount, Is.EqualTo(prev + 1));
        }


    }
}
