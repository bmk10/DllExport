﻿/*
 * The MIT License (MIT)
 * 
 * Copyright (c) 2016-2019  Denis Kuzmin < entry.reg@gmail.com > GitHub/3F
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
*/

// Via Cecil or direct modification:
//     
// https://github.com/3F/DllExport/issues/2#issuecomment-231593744
// 
//     000005B0                 00 C4 7B 01 00 00 00 2F 00 12 05       .Д{..../...
//     000005C0  00 00 02 00 00 00 00 00 00 00 00 00 00 00 26 00  ..............&.
//     000005D0  20 02 00 00 00 00 00 00 00 44 33 46 30 30 46 46   ........D3F00FF   <<<<
//     000005E0  31 37 37 30 44 45 44 39 37 38 45 43 37 37 34 42  1770DED978EC774B   <<<<...
//             
//     - - - -            
//     byte-seq via chars: 
//     + Identifier        = [32]bytes
//     + size of buffer    = [ 4]bytes (range: 0000 - FFF9; reserved: FFFA - FFFF)
//     + buffer of n size
//     - - - -
//     v1.2: 01F4 - allocated buffer size
// 

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using net.r_eg.Conari.Log;

namespace net.r_eg.DllExport.NSBin
{
    internal class Rmod: IDisposable
    {
        public const string IDNS        = "D3F00FF1770DED978EC774BA389F2DC9";
        public const string DEFAULT_NS  = "System.Runtime.InteropServices";
        public const int NS_BUF_MAX     = 0x01F4;

        private readonly string dll;
        private Ripper ripper;

        public ISender Log
        {
            get { return LSender._; }
        }

        public static bool IsValidNS(string name)
        {
            if(String.IsNullOrWhiteSpace(name)) {
                return true;
            }

            return Regex.IsMatch(name, @"^[^0-9\W][.\w]*$")
                && !Regex.IsMatch(name, @"(?:\.(\s*\.)+|\.\s*$)"); //  left. ...  .right.
        }

        public void setNamespace(string name, bool viaCecil, bool preparing)
        {
            Log.send(this, $"set new namespace(Cecil: {viaCecil}): ({name}) - ({dll})");

            if(String.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) {
                throw new FileNotFoundException($"The '{name}' assembly for modifications was not found.");
            }

            //if(String.IsNullOrWhiteSpace(name)) {
            //    throw new ArgumentException("The namespace cannot be null or empty.");
            //}

            if(preparing) {
                prepareLib(dll);
            }
            var ns = nsRule(name);

            if(viaCecil) {
                makeViaCecil(ns);
                return;
            }
            make(ns);
        }

        /// <param name="dll">The DllExport assembly for modifications.</param>
        /// <param name="enc"></param>
        public Rmod(string dll, Encoding enc)
        {
            this.dll    = dll;
            ripper      = new Ripper(dll, enc);
        }

        protected void makeViaCecil(string ns)
        {
            AssemblyDefinition asmdef = AssemblyDefinition.ReadAssembly
            (
                ripper.BaseStream,
                new ReaderParameters(ReadingMode.Immediate) { InMemory = true, ReadWrite = true }
            );

            update(asmdef, ns);

            //ripper.BaseStream.SetLength(0); // 0.9.x
            asmdef.Write(ripper.BaseStream); // https://github.com/3F/DllExport/pull/97#issuecomment-496319449
            asmdef.Dispose(); // 0.10.x

            using(var m = new Marker(_postfixToUpdated(dll)))
            {
                m.write(new MarkerData() {
                    viaCecil = true,
                    nsName = ns
                });
            }

            msgSuccess(ns);
        }

        protected void update(AssemblyDefinition asmdef, string ns)
        {
            foreach(TypeDefinition t in asmdef.MainModule.Types)
            {
                if(t.Namespace.StartsWith(IDNS, StringComparison.InvariantCulture))
                {
                    t.Namespace = ns;
                    return;
                }
            }

            throw new FileNotFoundException("IDNS sequence was not found.");
        }

        protected void make(string ns)
        {
            var ident = ripper.getBytesFrom(IDNS);

            byte[] data = ripper.readFirst64K();
            if(data.Length < ident.Length) {
                throw new FileNotFoundException("Incorrect size of library.");
            }

            int lpos = ripper.find(ident, ref data);

            if(lpos == -1) {
                throw new FileNotFoundException("Incorrect library.");
            }

            binmod(lpos, ident.Length, ns);
        }

        protected void binmod(int lpos, int ident, string ns)
        {
            Log.send(this, $"binmod: lpos({lpos}); ident({ident})");

            using(Stream stream = ripper.BaseStream)
            {
                stream.Seek(lpos + ident, SeekOrigin.Begin);

                ushort buffer = sysrange(
                    ripper.readHexStrAsUInt16()
                );

                byte[] nsBytes  = ripper.getBytesFrom(ns);
                int fullseq     = ident + (sizeof(UInt16) * 2) + buffer;

                Log.send(this, $"binmod: buffer({buffer}); fullseq({fullseq})");

                // the beginning of miracles

                var nsb = new List<byte>();
                nsb.AddRange(nsBytes);
                nsb.AddRange(Enumerable.Repeat((byte)0x00, fullseq - nsBytes.Length));

                stream.Seek(lpos, SeekOrigin.Begin);
                stream.Write(nsb.ToArray(), 0, nsb.Count);

                using(var m = new Marker(_postfixToUpdated(dll))) {
                    m.write(new MarkerData() { nsPosition = lpos, nsBuffer = buffer, nsName = ns });
                }

                msgSuccess(ns);
            }
        }

        protected virtual UInt16 sysrange(UInt16 val)
        {
            /* reserved: FFFA - FFFF */

            if(val < 0xFFFA) {
                return val;
            }

            var reserved = 0xFFFF - val;
            //switch(reserved) {
            //    // ...
            //}

            throw new NotImplementedException("The reserved combination is not yet implemented or not supported: " + reserved);
        }

        protected virtual string nsRule(string name)
        {
            // Regex.Replace(name, @"[\.\s]{1,}", ".").Trim(new char[] { '.', ' ' });

            if(IsValidNS(name)) {
                return name?.Replace(" ", "") ?? String.Empty;
            }
            return DEFAULT_NS;
        }

        protected void prepareLib(string dll)
        {
            string raw = $"{dll}.raw";

            // TODO: we can use ddNSi data, but do not forget about different methods - Cecil & Direct-mod
            if(!File.Exists(raw)) {
                File.Copy(dll, raw); //raw library before modifying - after installation or restoring
            }
            else {
                File.Copy(raw, dll, true); //when it possible: install for project A, then install for project B
            }
        }

        private void msgSuccess(string ns)
        {
            Log.send(this, "\nThe DllExport Library has been modified !\n");
            Log.send(this, $"namespace: '{ns}' :: {dll}");
            Log.send(this, "Details here: https://github.com/3F/DllExport/issues/2");
        }

        private string _postfixToUpdated(string dll)
        {
            return $"{dll}.ddNSi";
        }

        #region IDisposable

        // To detect redundant calls
        private bool disposed = false;

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if(disposed) {
                return;
            }
            disposed = true;

            //...
            if(ripper != null) {
                ripper.Dispose();
            }
        }

        #endregion
    }
}
