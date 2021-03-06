﻿// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license. 
// See license.txt file in the project root for full license information.

namespace Scriban.Syntax
{
    [ScriptSyntax("indexer expression", "<expression>[<index_expression>]")]
    public class ScriptIndexerExpression : ScriptVariablePath
    {
        public ScriptExpression Target { get; set; }

        public ScriptExpression Index { get; set; }

        public override object Evaluate(TemplateContext context)
        {
            return context.GetValue(this);
        }

        public override string ToString()
        {
            return $"{Target}[{Index}]";
        }
    }
}