﻿// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license. 
// See license.txt file in the project root for full license information.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Scriban.Helpers;

namespace Scriban.Parsing
{
    /// <summary>
    /// Lexer enumerator that generates <see cref="Token"/>, to use in a foreach.
    /// </summary>
    public class Lexer : IEnumerable<Token>
    {
        private TextPosition _position;
        private Token _token;
        private char c;
        private BlockType _blockType;
        private List<LogMessage> _errors;
        private int _openBraceCount;
        private int _escapeRawCharCount;
        private bool _isExpectingFrontMatter;

        private const char StripWhiteSpaceSpecialChar = '~';
        private const char RawEscapeSpecialChar = '%';

        /// <summary>
        /// Lexer options.
        /// </summary>
        public readonly LexerOptions Options;

        /// <summary>
        /// Initialize a new instance of this <see cref="Lexer" />.
        /// </summary>
        /// <param name="text">The text to analyze</param>
        /// <param name="sourcePath">The sourcePath</param>
        /// <param name="options">The options for the lexer</param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="System.ArgumentNullException"></exception>
        public Lexer(string text, string sourcePath = null, LexerOptions? options = null)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            Text = text;

            // Setup options
            var localOptions = options ?? LexerOptions.Default;
            if (localOptions.FrontMatterMarker == null)
            {
                localOptions.FrontMatterMarker = LexerOptions.DefaultFrontMatterMarker;
            }
            Options = localOptions;

            _position = Options.StartPosition;

            if (_position.Offset >= text.Length)
            {
                throw new ArgumentOutOfRangeException($"The starting position [{_position.Offset}] of range [0, {text.Length - 1}]");
            }

            SourcePath = sourcePath ?? "<input>";
            _blockType = Options.Mode == ScriptMode.ScriptOnly ? BlockType.Code : BlockType.Raw;

            _isExpectingFrontMatter = Options.Mode == ScriptMode.FrontMatterOnly ||
                                     Options.Mode == ScriptMode.FrontMatterAndContent;
        }

        /// <summary>
        /// Gets the text being parsed by this lexer
        /// </summary>
        public string Text { get; }

        /// <summary>
        /// 
        /// </summary>
        public string SourcePath { get; private set; }

        /// <summary>
        /// Gets a boolean indicating whether this lexer has errors.
        /// </summary>
        public bool HasErrors => _errors != null && _errors.Count > 0;

        /// <summary>
        /// Gets error messages.
        /// </summary>
        public IEnumerable<LogMessage> Errors => _errors ?? Enumerable.Empty<LogMessage>();

        /// <summary>
        /// Enumerator. Use simply <code>foreach</code> on this instance to automatically trigger an enumeration of the tokens.
        /// </summary>
        /// <returns></returns>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        private bool MoveNext()
        {
            var previousPosition = new TextPosition();
            bool isFirstLoop = true;

            while (true)
            {
                // If we have errors or we are already at the end of the file, we don't continue
                if (HasErrors || _token.Type == TokenType.Eof)
                {
                    return false;
                }

                if (_position.Offset == Text.Length)
                {
                    _token = Token.Eof;
                    return true;
                }

                // Safe guard in any case where the lexer has an error and loop forever (in case we forget to eat a token)
                if (!isFirstLoop && previousPosition == _position)
                {
                    throw new InvalidOperationException("Invalid internal state of the lexer in a forever loop");
                }

                isFirstLoop = false;
                previousPosition = _position;

                if (Options.Mode != ScriptMode.ScriptOnly)
                {
                    if (_blockType == BlockType.Raw)
                    {
                        bool skipPreviousSpaces;
                        if (IsCodeEnterOrEscape(out skipPreviousSpaces))
                        {
                            ReadCodeEnterOrEscape();
                            if (_blockType == BlockType.Code)
                            {
                                return true;
                            }
                        }
                        else if (_isExpectingFrontMatter && TryParseFrontMatterMarker())
                        {
                            _blockType = BlockType.Code;
                            return true;
                        }

                        // Else we have a BlockType.EscapeRaw, so we need to parse the raw block
                    }

                    if (_blockType != BlockType.Raw && IsCodeExit())
                    {
                        var wasInBlock = _blockType == BlockType.Code;
                        ReadCodeExitOrEscape();
                        if (wasInBlock)
                        {
                            return true;
                        }
                        // We are exiting from a BlockType.EscapeRaw, so we are back to a raw block or code, so we loop again
                        continue;
                    }

                    if (_blockType == BlockType.Code && _isExpectingFrontMatter && TryParseFrontMatterMarker())
                    {
                        // Once we have parsed a front matter, we don't expect them any longer
                        _blockType = BlockType.Raw;
                        _isExpectingFrontMatter = false;
                        return true;
                    }
                }

                // We may me directly at the end of the EOF without reading anykind of block
                // So we need to exit here
                if (_position.Offset == Text.Length)
                {
                    _token = Token.Eof;
                    return true;
                }

                if (_blockType == BlockType.Code)
                {
                    if (ReadCode())
                    {
                        break;
                    }
                }
                else
                {
                    if (ReadRaw())
                    {
                        break;
                    }
                }
            }

            return true;
        }

        private enum BlockType
        {
            Code,

            RawEscape,

            Raw,
        }

        private bool TryParseFrontMatterMarker()
        {
            var start = _position;
            var end = _position;

            var marker = Options.FrontMatterMarker;
            int i = 0;
            for (; i < marker.Length; i++)
            {
                if (PeekChar(i) != marker[i])
                {
                    return false;
                }
            }
            var pc = PeekChar(i);
            while (pc == ' ' || pc == '\t')
            {
                i++;
                pc = PeekChar(i);
            }

            bool valid = false;

            if (pc == '\n')
            {
                valid = true;
            }
            else if (pc == '\r')
            {
                valid = true;
                if (PeekChar(i + 1) == '\n')
                {
                    i++;
                }
            }

            if (valid)
            {
                while (i-- >= 0)
                {
                    end = _position;
                    NextChar();
                }

                _token = new Token(TokenType.FrontMatterMarker, start, end);
                return true;
            }

            return false;
        }

        private bool IsCodeEnterOrEscape(out bool removePreviousSpaces)
        {
            removePreviousSpaces = false;
            if (c == '{')
            {
                int i = 1;
                var nc = PeekChar(i);
                while (nc == RawEscapeSpecialChar)
                {
                    i++;
                    nc = PeekChar(i);
                }
                if (nc == '{')
                {
                    if (PeekChar(i + 1) == StripWhiteSpaceSpecialChar)
                    {
                        removePreviousSpaces = true;
                    }
                    return true;
                }
            }
            return false;
        }

        private void ReadCodeEnterOrEscape()
        {
            var start = _position;
            var end = _position;

            NextChar(); // Skip {

            while (c == RawEscapeSpecialChar)
            {
                _escapeRawCharCount++;
                end = end.NextColumn();
                NextChar();
            }

            end = end.NextColumn();
            NextChar(); // Skip {

            if (c == StripWhiteSpaceSpecialChar)
            {
                end = end.NextColumn();
                NextChar();
            }

            if (_escapeRawCharCount > 0)
            {
                _blockType = BlockType.RawEscape;
            }
            else
            {
                _blockType = BlockType.Code;
                _token = new Token(TokenType.CodeEnter, start, end);
            }
        }

        private bool IsCodeExit()
        {
            // Do we have any brace still opened? If yes, let ReadCode handle them
            if (_openBraceCount > 0)
            {
                return false;
            }

            // Do we have a ~}} or ~}%}
            int start = 0;
            if (c == StripWhiteSpaceSpecialChar)
            {
                start = 1;
            }

            // Do we have a regular 
            if (PeekChar(start) != '}')
            {
                return false;
            }

            start++;
            for (int i = 0; i < _escapeRawCharCount; i++)
            {
                if (PeekChar(i + start) != RawEscapeSpecialChar)
                {
                    return false;
                }
            }
            
            return PeekChar(_escapeRawCharCount + start) == '}';
        }

        private void ReadCodeExitOrEscape()
        {
            var start = _position;

            var shouldSkipSpacesAfterExit = false;
            if (c == StripWhiteSpaceSpecialChar)
            {
                shouldSkipSpacesAfterExit = true;
                NextChar();
            }

            NextChar();  // skip }
            for (int i = 0; i < _escapeRawCharCount; i++)
            {
                NextChar(); // skip !
            }
            var end = _position;
            NextChar(); // skip }

            if (_escapeRawCharCount > 0)
            {
                _escapeRawCharCount = 0;
            }
            else
            {
                _token = new Token(TokenType.CodeExit, start, end);
            }

            // Eat spaces after an exit
            if (shouldSkipSpacesAfterExit)
            {
                ConsumeWhitespace(false);
            }

            _blockType = BlockType.Raw;
        }

        private bool ReadRaw()
        {
            var start = _position;
            var end = start;
            bool nextCodeEnterOrEscapeExit = false;
            bool removePreviousSpaces = false;

            while (true)
            {
                if (PeekChar() != '\0')
                {
                    if (_blockType == BlockType.Raw && IsCodeEnterOrEscape(out removePreviousSpaces) || _blockType == BlockType.RawEscape && IsCodeExit())
                    {
                        nextCodeEnterOrEscapeExit = true;
                        break;
                    }

                    end = _position;
                    NextChar();
                }
                else
                {
                    break;
                }
            }


            if (removePreviousSpaces)
            {
                int i = -1;
                while (true)
                {
                    var testc = PeekChar(i);
                    if (char.IsWhiteSpace(testc) || testc == '\r')
                    {
                        if (testc == '\r')
                        {
                            end.Offset--;
                        }
                        else
                        {
                            end = testc == '\n' ? end.NextLine(-1) : end.NextColumn(-1);
                        }
                        i--;
                    }
                    else
                    {
                        break;
                    }
                }
                if (end.Offset < start.Offset)
                {
                    return false;
                }
            }

            _token = new Token(TokenType.Raw, start, nextCodeEnterOrEscapeExit ? end : _position);

            // Go to eof
            if (!nextCodeEnterOrEscapeExit)
            {
                NextChar();
            }

            return true;
        }

        private bool ReadCode()
        {
            bool hasTokens = true;
            var start = _position;
            switch (c)
            {
                case '\n':
                    _token = new Token(TokenType.NewLine, start, _position);
                    NextChar();
                    // consume all remaining space including new lines
                    ConsumeWhitespace(false);
                    break;
                case ';':
                    _token = new Token(TokenType.SemiColon, start, _position);
                    NextChar();
                    // consume all remaining space including new lines
                    ConsumeWhitespace(false);
                    break;
                case '\r':
                    NextChar();
                    // case of: \r\n
                    if (c == '\n')
                    {
                        _token = new Token(TokenType.NewLine, start, _position);
                        NextChar();
                        // consume all remaining space including new lines
                        ConsumeWhitespace(false);
                        break;
                    }
                    // case of \r
                    _token = new Token(TokenType.NewLine, start, start);
                    // consume all remaining space including new lines
                    ConsumeWhitespace(false);
                    break;
                case ':':
                    _token = new Token(TokenType.Colon, start, start);
                    NextChar();
                    break;
                case '@':
                    _token = new Token(TokenType.Arroba, start, start);
                    NextChar();
                    break;
                case '^':
                    _token = new Token(TokenType.Caret, start, start);
                    NextChar();
                    break;
                case '*':
                    _token = new Token(TokenType.Multiply, start, start);
                    NextChar();
                    break;
                case '/':
                    NextChar();
                    if (c == '/')
                    {
                        _token = new Token(TokenType.DoubleDivide, start, _position);
                        NextChar();
                        break;
                    }
                    _token = new Token(TokenType.Divide, start, start);
                    break;
                case '+':
                    _token = new Token(TokenType.Plus, start, start);
                    NextChar();
                    break;
                case '-':
                    _token = new Token(TokenType.Minus, start, start);
                    NextChar();
                    break;
                case '%':
                    _token = new Token(TokenType.Modulus, start, start);
                    NextChar();
                    break;
                case ',':
                    _token = new Token(TokenType.Comma, start, start);
                    NextChar();
                    break;
                case '&':
                    NextChar();
                    if (c == '&')
                    {
                        _token = new Token(TokenType.And, start, _position);
                        NextChar();
                        break;
                    }

                    // & is an invalid char alone
                    _token = new Token(TokenType.Invalid, start, start);
                    break;
                case '?':
                    NextChar();
                    if (c == '?')
                    {
                        _token = new Token(TokenType.EmptyCoalescing, start, _position);
                        NextChar();
                        break;
                    }

                    // ? is an invalid char alone
                    _token = new Token(TokenType.Invalid, start, start);
                    break;
                case '|':
                    NextChar();
                    if (c == '|')
                    {
                        _token = new Token(TokenType.Or, start, _position);
                        NextChar();
                        break;
                    }
                    _token = new Token(TokenType.Pipe, start, start);
                    break;
                case '.':
                    NextChar();
                    if (c == '.')
                    {
                        var index = _position;
                        NextChar();
                        if (c == '<')
                        {
                            _token = new Token(TokenType.DoubleDotLess, start, _position);
                            NextChar();
                            break;
                        }

                        _token = new Token(TokenType.DoubleDot, start, index);
                        break;
                    }
                    _token = new Token(TokenType.Dot, start, start);
                    break;

                case '!':
                    NextChar();
                    if (c == '=')
                    {
                        _token = new Token(TokenType.CompareNotEqual, start, _position);
                        NextChar();
                        break;
                    }
                    _token = new Token(TokenType.Not, start, start);
                    break;

                case '=':
                    NextChar();
                    if (c == '=')
                    {
                        _token = new Token(TokenType.CompareEqual, start, _position);
                        NextChar();
                        break;
                    }
                    _token = new Token(TokenType.Equal, start, start);
                    break;
                case '<':
                    NextChar();
                    if (c == '=')
                    {
                        _token = new Token(TokenType.CompareLessOrEqual, start, _position);
                        NextChar();
                        break;
                    }
                    if (c == '<')
                    {
                        _token = new Token(TokenType.ShiftLeft, start, _position);
                        NextChar();
                        break;
                    }
                    _token = new Token(TokenType.CompareLess, start, start);
                    break;
                case '>':
                    NextChar();
                    if (c == '=')
                    {
                        _token = new Token(TokenType.CompareGreaterOrEqual, start, _position);
                        NextChar();
                        break;
                    }
                    if (c == '>')
                    {
                        _token = new Token(TokenType.ShiftRight, start, _position);
                        NextChar();
                        break;
                    }
                    _token = new Token(TokenType.CompareGreater, start, start);
                    break;
                case '(':
                    _token = new Token(TokenType.OpenParent, _position, _position);
                    NextChar();
                    break;
                case ')':
                    _token = new Token(TokenType.CloseParent, _position, _position);
                    NextChar();
                    break;
                case '[':
                    _token = new Token(TokenType.OpenBracket, _position, _position);
                    NextChar();
                    break;
                case ']':
                    _token = new Token(TokenType.CloseBracket, _position, _position);
                    NextChar();
                    break;
                case '{':
                    // We count brace open to match then correctly later and avoid confusing with code exit
                    _openBraceCount++;
                    _token = new Token(TokenType.OpenBrace, _position, _position);
                    NextChar();
                    break;
                case '}':
                    if (_openBraceCount > 0)
                    {
                        // We match first brace open/close
                        _openBraceCount--;
                        _token = new Token(TokenType.CloseBrace, _position, _position);
                        NextChar();
                    }
                    else
                    {
                        if (Options.Mode != ScriptMode.ScriptOnly && IsCodeExit())
                        {
                            // We have no tokens for this ReadCode
                            hasTokens = false;
                        }
                        else
                        {
                            // Else we have a close brace but it is invalid
                            _token = new Token(TokenType.CloseBrace, _position, _position);
                            AddError("Unexpected } while no matching {", _position, _position);
                            NextChar();
                        }
                    }
                    break;
                case '#':
                    ReadComment();
                    break;
                case '"':
                case '\'':
                    ReadString();
                    break;
                case '`':
                    ReadVerbatimString();
                    break;
                case '\0':
                    _token = Token.Eof;
                    break;
                default:
                    // Eat any whitespace
                    if (ConsumeWhitespace(true))
                    {
                        // We have no tokens for this ReadCode
                        hasTokens = false;
                        break;
                    }

                    bool specialIdentifier = c == '$';
                    if (IsFirstIdentifierLetter(c) || specialIdentifier)
                    {
                        ReadIdentifier(specialIdentifier);
                        break;
                    }

                    if (char.IsDigit(c))
                    {
                        ReadNumber();
                        break;
                    }

                    // invalid char
                    _token = new Token(TokenType.Invalid, _position, _position);
                    NextChar();
                    break;
            }

            return hasTokens;
        }

        private bool ConsumeWhitespace(bool stopAtNewLine)
        {
            var start = _position;
            while (char.IsWhiteSpace(c) && (!stopAtNewLine || !IsNewLine(c)))
            {
                NextChar();
            }
            return start != _position;
        }

        private static bool IsNewLine(char c)
        {
            return c == '\n';
        }

        private void ReadIdentifier(bool special)
        {
            var start = _position;

            TextPosition beforePosition;
            bool first = true;
            do
            {
                beforePosition = _position;
                NextChar();

                // Special $$ variable allowed only here
                if (first && special && c == '$')
                {
                    _token = new Token(TokenType.IdentifierSpecial, start, _position);
                    NextChar();
                    return;
                }

                first = false;
            } while (IsIdentifierLetter(c));

            _token = new Token(special ? TokenType.IdentifierSpecial : TokenType.Identifier, start, beforePosition);
        }

        [MethodImpl(MethodImplOptionsPortable.AggressiveInlining)]
        private static bool IsFirstIdentifierLetter(char c)
        {
            return c == '_' || char.IsLetter(c);
        }

        [MethodImpl(MethodImplOptionsPortable.AggressiveInlining)]
        private static bool IsIdentifierLetter(char c)
        {
            return IsFirstIdentifierLetter(c) || char.IsDigit(c);
        }

        private void ReadNumber()
        {
            var start = _position;
            var end = _position;
            var hasDot = false;

            // Read first part
            do
            {
                end = _position;
                NextChar();
            } while (char.IsDigit(c));


            // Read any number following
            if (c == '.')
            {
                // If the next char is a '.' it means that we have a range iterator, so we don't touch it
                if (PeekChar() != '.')
                {
                    hasDot = true;
                    end = _position;
                    NextChar();
                    while (char.IsDigit(c))
                    {
                        end = _position;
                        NextChar();
                    }
                }
            }

            if (c == 'e' || c == 'E')
            {
                end = _position;
                NextChar();
                if (c == '+' || c == '-')
                {
                    end = _position;
                    NextChar();
                }

                if (!char.IsDigit(c))
                {
                    AddError("Expecting at least one digit after the exponent", _position, _position);
                    return;
                }

                while (char.IsDigit(c))
                {
                    end = _position;
                    NextChar();
                }
            }

            _token = new Token(hasDot ? TokenType.Float : TokenType.Integer, start, end);
        }

        private void ReadString()
        {
            var start = _position;
            var end = _position;
            char startChar = c;
            NextChar(); // Skip "
            while (true)
            {
                if (c == '\\')
                {
                    end = _position;
                    NextChar();
                    // 0 ' " \ b f n r t v u0000-uFFFF x00-xFF
                    switch (c)
                    {
                        case '\n':
                            end = _position;
                            NextChar();
                            continue;
                        case '\r':
                            end = _position;
                            NextChar();
                            if (c == '\n')
                            {
                                end = _position;
                                NextChar();
                            }
                            continue;
                        case '0':
                        case '\'':
                        case '"':
                        case '\\':
                        case 'b':
                        case 'f':
                        case 'n':
                        case 'r':
                        case 't':
                        case 'v':
                            end = _position;
                            NextChar();
                            continue;
                        case 'u':
                            end = _position;
                            NextChar();
                            // Must be followed 4 hex numbers (0000-FFFF)
                            if (c.IsHex()) // 1
                            {
                                end = _position;
                                NextChar();
                                if (c.IsHex()) // 2
                                {
                                    end = _position;
                                    NextChar();
                                    if (c.IsHex()) // 3
                                    {
                                        end = _position;
                                        NextChar();
                                        if (c.IsHex()) // 4
                                        {
                                            end = _position;
                                            NextChar();
                                            continue;
                                        }
                                    }
                                }
                            }
                            break;
                        case 'x':
                            end = _position;
                            NextChar();
                            // Must be followed 2 hex numbers (00-FF)
                            if (c.IsHex())
                            {
                                end = _position;
                                NextChar();
                                if (c.IsHex())
                                {
                                    end = _position;
                                    NextChar();
                                    continue;
                                }
                            }
                            break;

                    }
                    AddError($"Unexpected escape character [{c}] in string. Only 0 ' \\ \" b f n r t v u0000-uFFFF x00-xFF are allowed", _position, _position);
                }
                else if (c == '\0')
                {
                    AddError($"Unexpected end of file while parsing a string not terminated by a {startChar}", end, end);
                    return;
                }
                else if (c == startChar)
                {
                    end = _position;
                    NextChar();
                    break;
                }
                else
                {
                    end = _position;
                    NextChar();
                }
            }

            _token = new Token(TokenType.String, start, end);
        }

        private void ReadVerbatimString()
        {
            var start = _position;
            var end = _position;
            char startChar = c;
            NextChar(); // Skip `
            while (true)
            {
                if (c == '\0')
                {
                    AddError($"Unexpected end of file while parsing a string not terminated by a {startChar}", end, end);
                    return;
                }
                else if (c == startChar)
                {
                    end = _position;
                    NextChar(); // Do we have an escape?
                    if (c != startChar)
                    {
                        break;
                    }
                    end = _position;
                    NextChar();
                }
                else
                {
                    end = _position;
                    NextChar();
                }
            }

            _token = new Token(TokenType.VerbatimString, start, end);
        }

        private void ReadComment()
        {
            var start = _position;
            var end = _position;

            NextChar();

            // Is Multiline?
            bool isMulti = false;
            if (c == '#')
            {
                isMulti = true;

                end = _position;
                NextChar();

                while (!IsCodeExit())
                {
                    if (c == '\0')
                    {
                        break;
                    }

                    var mayBeEndOfComment = c == '#';

                    end = _position;
                    NextChar();

                    if (mayBeEndOfComment && c == '#')
                    {
                        end = _position;
                        NextChar();
                        break;
                    }
                }

            }
            else
            {
                while (!IsCodeExit())
                {
                    if (c == '\0' || c == '\r' || c == '\n')
                    {
                        break;
                    }
                    end = _position;
                    NextChar();
                }
            }

            _token = new Token(isMulti ? TokenType.CommentMulti : TokenType.Comment, start, end);
        }

        [MethodImpl(MethodImplOptionsPortable.AggressiveInlining)]
        private char PeekChar(int count = 1)
        {
            var offset = _position.Offset + count;

            return offset >= 0 && offset < Text.Length ? Text[offset] : '\0';
        }

        [MethodImpl(MethodImplOptionsPortable.AggressiveInlining)]
        private void NextChar()
        {
            _position.Offset++;
            if (_position.Offset < Text.Length)
            {
                if (c == '\n')
                {
                    _position.Column = 0;
                    _position.Line += 1;
                }
                else
                {
                    _position.Column++;
                }
                c = Text[_position.Offset];
            }
            else
            {
                _position.Offset = Text.Length;
                c = '\0';
            }
        }

        IEnumerator<Token> IEnumerable<Token>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private void AddError(string message, TextPosition start, TextPosition end)
        {
            _token = new Token(TokenType.Invalid, start, end);
            if (_errors == null)
            {
                _errors = new List<LogMessage>();
            }
            _errors.Add(new LogMessage(ParserMessageType.Error, new SourceSpan(SourcePath, start, end), message));
        }


        private void Reset()
        {
            c = Text.Length > 0 ? Text[Options.StartPosition.Offset] : '\0';
            _position = Options.StartPosition;
            _errors = null;
        }

        /// <summary>
        /// Custom enumerator on <see cref="Token"/>
        /// </summary>
        public struct Enumerator : IEnumerator<Token>
        {
            private readonly Lexer lexer;

            public Enumerator(Lexer lexer)
            {
                this.lexer = lexer;
                lexer.Reset();
            }

            public bool MoveNext()
            {
                return lexer.MoveNext();
            }

            public void Reset()
            {
                lexer.Reset();
            }

            public Token Current => lexer._token;

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }
        }
    }
}
