// Copyright (c) 2020, mParticle, Inc
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//   https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.IO;
using System.Text;

namespace MP.Json.Cli
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            var utf8 = new UTF8Encoding(false);
            using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
            using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };

            int exitCode = ValidatorCommand.Run(args, stdin, stdout, stderr);
            stdout.Flush();
            return exitCode;
        }
    }
}
