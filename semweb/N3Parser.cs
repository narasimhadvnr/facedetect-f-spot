using System;
using System.Collections;
using System.IO;
using System.Text;

using SemWeb;
using SemWeb.Util;

namespace SemWeb {

	public class N3Reader : RdfReader {
		Resource PrefixResource = new Literal("@prefix");
		Resource KeywordsResource = new Literal("@keywords");
		
		TextReader sourcestream;
		NamespaceManager namespaces = new NamespaceManager();

		Entity entRDFTYPE = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
		Entity entRDFFIRST = "http://www.w3.org/1999/02/22-rdf-syntax-ns#first";
		Entity entRDFREST = "http://www.w3.org/1999/02/22-rdf-syntax-ns#rest";
		Entity entRDFNIL = "http://www.w3.org/1999/02/22-rdf-syntax-ns#nil";
		//Entity entOWLSAMEAS = "http://www.w3.org/2002/07/owl#sameAs";
		Entity entDAMLEQUIV = "http://www.daml.org/2000/12/daml+oil#equivalentTo";
		Entity entLOGIMPLIES = "http://www.w3.org/2000/10/swap/log#implies";
		
		public N3Reader(TextReader source) {
			this.sourcestream = source;
		}
		
		public N3Reader(string sourcefile) {
			this.sourcestream = GetReader(sourcefile);
			BaseUri = "file:" + sourcefile + "#";
		}

		private struct ParseContext {
			public MyReader source;
			public StatementSink store;
			public NamespaceManager namespaces;
			public UriMap namedNode;
			public Hashtable anonymous;
			public Entity meta;
			public bool UsingKeywords;
			public Hashtable Keywords;
			
			public Location Location { get { return new Location(source.Line, source.Col); } }
		}
		
		public override void Select(StatementSink store) {
			ParseContext context = new ParseContext();
			context.source = new MyReader(sourcestream);
			context.store = GetDupCheckSink(store);
			context.namespaces = namespaces;
			context.namedNode = new UriMap();
			context.anonymous = new Hashtable();
			context.meta = Meta;
			
			while (ReadStatement(context)) { }
		}
		
		private bool ReadStatement(ParseContext context) {
			Location loc = context.Location;
			
			bool reverse;
			Resource subject = ReadResource(context, out reverse);
			if (subject == null) return false;
			if (reverse) OnError("is...of not allowed on a subject", loc);
			
			if ((object)subject == (object)PrefixResource) {
				loc = context.Location;
				string qname = ReadToken(context.source, context) as string;
				if (qname == null || !qname.EndsWith(":")) OnError("When using @prefix, the prefix identifier must end with a colon", loc);
				
				loc = context.Location;
				Resource uri = ReadResource(context, out reverse);
				if (uri == null) OnError("Expecting a URI", loc);
				if (reverse) OnError("is...of not allowed here", loc);
				namespaces.AddNamespace(uri.Uri, qname.Substring(0, qname.Length-1));
				
				loc = context.Location;
				char punc = ReadPunc(context.source);
				if (punc != '.')
					OnError("Expected a period but found '" + punc + "'", loc);
				return true;
			}
			
			if ((object)subject == (object)KeywordsResource) {
				context.UsingKeywords = true;
				context.Keywords = new Hashtable();
				while (true) {
					ReadWhitespace(context.source);
					if (context.source.Peek() == '.') {
						context.source.Read();
						break;
					}
				
					loc = context.Location;
					string tok = ReadToken(context.source, context) as string;
					if (tok == null)
						OnError("Expecting keyword names", loc);
						
					context.Keywords[tok] = tok;
				}
				return true;
			}
			
			// It's possible to just assert the presence of an entity
			// by following the entity with a period, or a } to end
			// a reified context.
			if (NextPunc(context.source) == '.') {
				context.source.Read();
				return true;
			}
			if (NextPunc(context.source) == '}') {
				context.source.Read();
				return false; // end of block
			}
			
			// Read the predicates for this subject.
			char period = ReadPredicates(subject, context);
			loc = context.Location;
			if (period != '.' && period != '}')
				OnError("Expected a period but found '" + period + "'", loc);
			if (period == '}') return false;
			return true;
		}
		
		private char ReadPredicates(Resource subject, ParseContext context) {			
			char punctuation = ';';
			while (punctuation == ';')
				punctuation = ReadPredicate(subject, context);
			return punctuation;
		}
		
		private char ReadPredicate(Resource subject, ParseContext context) {
			bool reverse;
			Location loc = context.Location;
			Resource predicate = ReadResource(context, out reverse);
			if (predicate == null) OnError("Expecting a predicate", loc);
			if (predicate is Literal) OnError("Predicates cannot be literals", loc);
			
			char punctuation = ',';
			while (punctuation == ',') {
				ReadObject(subject, (Entity)predicate, context, reverse);
				loc = context.Location;
				punctuation = ReadPunc(context.source);
			}
			if (punctuation != '.' && punctuation != ';' && punctuation != ']' && punctuation != '}')
				OnError("Expecting a period, semicolon, comma, or close-bracket but found '" + punctuation + "'", loc);
			
			return punctuation;
		}
		
		private void ReadObject(Resource subject, Entity predicate, ParseContext context, bool reverse) {
			bool reverse2;
			Location loc = context.Location;
			Resource value = ReadResource(context, out reverse2);
			if (value == null) OnError("Expecting a resource or literal object", loc);
			if (reverse2) OnError("is...of not allowed on objects", loc);
			
			loc = context.Location;
			if (!reverse) {
				if (subject is Literal) OnError("Subjects of statements cannot be literals", loc);			
				Add(context.store, new Statement((Entity)subject, predicate, value, context.meta), loc);
			} else {
				if (value is Literal) OnError("A literal cannot be the object of a reverse-predicate statement", loc);
				Add(context.store, new Statement((Entity)value, predicate, subject, context.meta), loc);
			}
		}
		
		private void ReadWhitespace(MyReader source) {
			while (true) {
				while (char.IsWhiteSpace((char)source.Peek()))
					source.Read();
				
				if (source.Peek() == '#') {
					while (true) {
						int c = source.Read();
						if (c == -1 || c == 10 || c == 13) break;
					}
					continue;
				}
				
				break;
			}
		}
		
		private char ReadPunc(MyReader source) {
			ReadWhitespace(source);
			int c = source.Read();
			if (c == -1)
				OnError("End of file expecting punctuation", new Location(source.Line, source.Col));
			return (char)c;
		}
		
		private int NextPunc(MyReader source) {
			ReadWhitespace(source);
			return source.Peek();
		}
		
		private void ReadEscapedChar(char c, StringBuilder b, MyReader source, Location loc) {
			if (c == 'n') b.Append('\n');
			else if (c == 'r') b.Append('\r');
			else if (c == 't') b.Append('\t');
			else if (c == '\\') b.Append('\\');		
			else if (c == '"') b.Append('"');
			else if (c == '\'') b.Append('\'');
			else if (c == 'a') b.Append('\a');
			else if (c == 'b') b.Append('\b');
			else if (c == 'f') b.Append('\f');
			else if (c == 'v') b.Append('\v');
			else if (c == '\n') { }
			else if (c == '\r') { }
			else if (c == 'u' || c == 'U') {
				StringBuilder num = new StringBuilder();
				if (c == 'u')  {
					num.Append((char)source.Read()); // four hex digits
					num.Append((char)source.Read());
					num.Append((char)source.Read());
					num.Append((char)source.Read());
				} else {
					source.Read(); // two zeros
					source.Read();
					num.Append((char)source.Read()); // six hex digits
					num.Append((char)source.Read());
					num.Append((char)source.Read());
					num.Append((char)source.Read());
					num.Append((char)source.Read());
					num.Append((char)source.Read());
				}
				
				int unicode = int.Parse(num.ToString(), System.Globalization.NumberStyles.AllowHexSpecifier);
				b.Append((char)unicode); // is this correct?
				
			} else if (char.IsDigit((char)c) || c == 'x')
				OnError("Octal and hex byte-value escapes are deprecated and not supported", loc);
			else
				OnError("Unrecognized escape character: " + (char)c, loc);
		}
		
		private StringBuilder readTokenBuffer = new StringBuilder();
		
		private object ReadToken(MyReader source, ParseContext context) {
			ReadWhitespace(source);
			
			Location loc = new Location(source.Line, source.Col);
			
			int firstchar = source.Read();
			if (firstchar == -1)
				return "";
			
			StringBuilder b = readTokenBuffer; readTokenBuffer.Length = 0;
			b.Append((char)firstchar);

			if (firstchar == '<') {
				// This is a URI or the <= verb.  URIs can be escaped like strings, at least in the NTriples spec.
				bool escaped = false;
				while (true) {
					int c = source.Read();
					if (c == -1) OnError("Unexpected end of stream within a token beginning with <", loc);
					
					if (b.Length == 2 && c == '=')
						return "<="; // the <= verb
					
					if (escaped) {
						ReadEscapedChar((char)c, b, source, loc);
						escaped = false;
					} else if (c == '\\') {
						escaped = true;
					} else {
						b.Append((char)c);
						if (c == '>') // end of the URI
							break;
					}
				}
				
			} else if (firstchar == '"') {
				// A string: ("""[^"\\]*(?:(?:\\.|"(?!""))[^"\\]*)*""")|("[^"\\]*(?:\\.[^"\\]*)*")
				// What kind of crazy regex is this??
				b.Length = 0; // get rid of the open quote
				bool escaped = false;
				bool triplequoted = false;
				while (true) {
					int c = source.Read();
					if (c == -1) OnError("Unexpected end of stream within a string", loc);
					
					if (b.Length == 0 && c == (int)'"' && source.Peek() == (int)'"') {
						triplequoted = true;
						source.Read();
						continue;
					}
					
					if (!escaped && c == '\\')
						escaped = true;
					else if (escaped) {
						ReadEscapedChar((char)c, b, source, loc);
						escaped = false;
					} else {
						if (c == '"' && !triplequoted)
							break;
						if (c == '"' && source.Peek() == '"' && source.Peek2() == '"' && triplequoted)
							break;
						b.Append((char)c);
					}
				}
				
				if (triplequoted) { // read the extra end quotes
					source.Read();
					source.Read();
				}
				
				string litvalue = b.ToString();
				string litlang = null;
				string litdt = null;

				// Strings can be suffixed with @langcode or ^^symbol (but not both?).
				if (source.Peek() == '@') {
					source.Read();
					b.Length = 0;
					while (char.IsLetterOrDigit((char)source.Peek()) || source.Peek() == (int)'-')
						b.Append((char)source.Read());
					litlang = b.ToString();
				} else if (source.Peek() == '^' && source.Peek2() == '^') {
					loc = new Location(source.Line, source.Col);
					source.Read();
					source.Read();
					litdt = ReadToken(source, context).ToString(); // better be a string URI
					if (litdt.StartsWith("<") && litdt.EndsWith(">"))
						litdt = litdt.Substring(1, litdt.Length-2);
					else if (litdt.IndexOf(":") != -1) {
						Resource r = ResolveQName(litdt, context, loc);
						if (r.Uri == null)
							OnError("A literal datatype cannot be an anonymous entity", loc);
						litdt = r.Uri;
					}
				}
				
				return new Literal(litvalue, litlang, litdt);

			} else if (char.IsLetter((char)firstchar) || firstchar == '?' || firstchar == '@' || firstchar == ':' || firstchar == '_') {
				// Something starting with @
				// A QName: ([a-zA-Z_][a-zA-Z0-9_]*)?:)?([a-zA-Z_][a-zA-Z0-9_]*)?
				// A variable: \?[a-zA-Z_][a-zA-Z0-9_]*
				while (true) {
					int c = source.Peek();
					if (c == -1 || (!char.IsLetterOrDigit((char)c) && c != '-' && c != '_' && c != ':')) break;					
					b.Append((char)source.Read());
				}
			
			} else if (char.IsDigit((char)firstchar) || firstchar == '+' || firstchar == '-') {
				while (true) {
					int ci = source.Peek();
					if (ci == -1) break;
					
					// punctuation followed by a space means the punctuation is
					// punctuation, and not part of this token
					if (!char.IsDigit((char)ci) && source.Peek2() != -1 && char.IsWhiteSpace((char)source.Peek2()))
						break;
					
					char c = (char)ci;
					if (char.IsWhiteSpace(c)) break;
					
					b.Append((char)source.Read());
				}
				
			} else if (firstchar == '=') {
				if (source.Peek() == (int)'>')
					b.Append((char)source.Read());
			
			} else if (firstchar == '[') {
				// The start of an anonymous node.

			} else if (firstchar == '{') {
				return "{";

			} else if (firstchar == '(') {
				return "(";
			} else if (firstchar == ')') {
				return ")";

			} else {
				while (true) {
					int c = source.Read();
					if (c == -1) break;
					if (char.IsWhiteSpace((char)c)) break;
					b.Append((char)c);
				}
				OnError("Invalid token: " + b.ToString(), loc);
			}
			
			return b.ToString();
		}
		
		private Resource ReadResource(ParseContext context, out bool reverse) {
			Location loc = context.Location;
			
			Resource res = ReadResource2(context, out reverse);
			
			ReadWhitespace(context.source);
			while (context.source.Peek() == '!' || context.source.Peek() == '^' || (context.source.Peek() == '.' && context.source.Peek2() != -1 && char.IsLetter((char)context.source.Peek2())) ) {
				int pathType = context.source.Read();
				
				bool reverse2;
				loc = context.Location;
				Resource path = ReadResource2(context, out reverse2);
				if (reverse || reverse2) OnError("is...of is not allowed in path expressions", loc);
				if (!(path is Entity)) OnError("A path expression cannot be a literal", loc);
				
				Entity anon = new Entity(null);
				
				Statement s;
				if (pathType == '!' || pathType == '.') {
					if (!(res is Entity)) OnError("A path expression cannot contain a literal: " + res, loc);
					s = new Statement((Entity)res, (Entity)path, anon, context.meta);
				} else {
					s = new Statement(anon, (Entity)path, res, context.meta);
				}
				
				Add(context.store, s, loc);
				
				res = anon;

				ReadWhitespace(context.source);
			}
				
			return res;
		}			
		
		private Entity GetResource(ParseContext context, string uri) {
			if (!ReuseEntities)
				return new Entity(uri);
		
			Entity ret = (Entity)context.namedNode[uri];
			if (ret != null) return ret;
			ret = new Entity(uri);
			context.namedNode[uri] = ret;
			return ret;
		}

		private Resource ResolveQName(string str, ParseContext context, Location loc) {
			int colon = str.IndexOf(":");
			string prefix = str.Substring(0, colon);
			if (prefix == "_") {
				Resource ret = (Resource)context.anonymous[str];
				if (ret == null) {
					ret = new Entity(null);
					context.anonymous[str] = ret;
				}
				return ret;
			} else if (prefix == "") {
				return GetResource(context, (BaseUri == null ? "" : BaseUri) + str.Substring(colon+1));
			} else {
				string ns = context.namespaces.GetNamespace(prefix);
				if (ns == null)
					OnError("Prefix is undefined: " + str, loc);
				return GetResource(context, ns + str.Substring(colon+1));
			}
		}
			
		private Resource ReadResource2(ParseContext context, out bool reverse) {
			reverse = false;
			
			Location loc = context.Location;
			
			object tok = ReadToken(context.source, context);
			if (tok is Literal)
				return (Literal)tok;
			
			string str = (string)tok;
			if (str == "")
				return null;
			
			// @ Keywords

			if (str == "@prefix")
				return PrefixResource;

			if (str == "@keywords")
				return KeywordsResource;
			
			if (context.UsingKeywords && context.Keywords.Contains(str))
				str = "@" + str;
			if (!context.UsingKeywords &&
				( str == "a" || str == "has" || str == "is"))
				str = "@" + str;
			
			// Standard Keywords
			// TODO: Turn these off with @keywords
			
			if (str == "@a")
				return entRDFTYPE;
				
			if (str == "=")
				return entDAMLEQUIV;
			if (str == "=>")
				return entLOGIMPLIES;
			if (str == "<=") {
				reverse = true;
				return entLOGIMPLIES;
			}				

			if (str == "@has") // ignore this token
				return ReadResource2(context, out reverse);
			
			if (str == "@is") {
				// Reverse predicate
				bool reversetemp;
				Resource pred = ReadResource2(context, out reversetemp);
				reverse = true;
				
				string of = ReadToken(context.source, context) as string;
				if (of == null) OnError("End of stream while expecting 'of'", loc);
				if (of == "@of"
					|| (!context.UsingKeywords && of == "of")
					|| (context.UsingKeywords && context.Keywords.Contains("of") && of == "of"))
					return pred;
				OnError("Expecting token 'of' but found '" + of + "'", loc);
				return null; // unreachable
			}
			
			if (str.StartsWith("@"))
				OnError("The " + str + " directive is not supported", loc);

			// URI
			
			if (str.StartsWith("<") && str.EndsWith(">")) {
				string uri = GetAbsoluteUri(BaseUri, str.Substring(1, str.Length-2));
				return GetResource(context, uri);
			}
			
			// VARIABLE
			
			if (str[0] == '?') {
				string uri = str.Substring(1);
				if (BaseUri != null)
					uri = BaseUri + uri;
				Entity var = GetResource(context, uri);
				AddVariable(var);
				return var;
			}
			
			// QNAME

			if (str.IndexOf(":") != -1)
				return ResolveQName(str, context, loc);
				
			// ANONYMOUS
			
			if (str == "[") {
				Entity ret = new Entity(null);
				ReadWhitespace(context.source);
				if (context.source.Peek() != ']') {
					char bracket = ReadPredicates(ret, context);
					if (bracket == '.')
						bracket = ReadPunc(context.source);
					if (bracket != ']')
						OnError("Expected a close bracket but found '" + bracket + "'", loc);
				} else {
					context.source.Read();
				}
				return ret;
			}
			
			// LIST
			
			if (str == "(") {
				// A list
				Entity ent = null;
				while (true) {
					bool rev2;
					Resource res = ReadResource(context, out rev2);
					if (res == null)
						break;
					
					if (ent == null) {
						ent = new Entity(null);
					} else {
						Entity sub = new Entity(null);
						Add(context.store, new Statement(ent, entRDFREST, sub, context.meta), loc);
						ent = sub;
					}
					
					Add(context.store, new Statement(ent, entRDFFIRST, res, context.meta), loc);
				}
				if (ent == null) // No list items.
					ent = entRDFNIL; // according to Turtle spec
				else
					Add(context.store, new Statement(ent, entRDFREST, entRDFNIL, context.meta), loc);
				return ent;
			}
			
			if (str == ")")
				return null; // Should I use a more precise end-of-list return value?
			
			// REIFICATION
			
			if (str == "{") {
				// Embedded resource
				ParseContext newcontext = context;
				newcontext.meta = new Entity(null);
				while (NextPunc(context.source) != '}' && ReadStatement(newcontext)) { }
				ReadWhitespace(context.source);
				if (context.source.Peek() == '}') context.source.Read();
				return newcontext.meta;
			}
			
			// NUMERIC LITERAL
			
			// In Turtle, numbers are restricted to [0-9]+, and are datatyped xsd:integer.
			double numval;
			if (double.TryParse(str, System.Globalization.NumberStyles.Any, null, out numval))
				return new Literal(numval.ToString());
			
			// If @keywords is used, alphanumerics that aren't keywords
			// are local names in the default namespace.
			if (context.UsingKeywords && char.IsLetter(str[0])) {
				if (BaseUri == null)
					OnError("The document contains an unqualified name but no BaseUri was specified: \"" + str + "\"", loc);
				return GetResource(context, BaseUri + str);
			}
			
			// NOTHING MATCHED
			
			OnError("Invalid token: " + str, loc);
			return null;
		}
		
		private void Add(StatementSink store, Statement statement, Location position) {
			try {
				store.Add(statement);
			} catch (Exception e) {
				OnError("Add failed on statement { " + statement + " }: " + e.Message, position, e);
			}
		}
		
		private void OnError(string message, Location position) {
			throw new ParserException(message + ", line " + position.Line + " col " + position.Col);
		}
		private void OnError(string message, Location position, Exception cause) {
			throw new ParserException(message + ", line " + position.Line + " col " + position.Col, cause);
		}
		
	
	}

	internal class MyReader {
		TextReader r;
		public MyReader(TextReader reader) { r = reader; }
		
		public int Line = 1;
		public int Col = 0;
		
		int[] peeked = new int[2];
		int peekCount = 0;
		
		public Location Location { get { return new Location(Line, Col); } }
		
		public int Peek() {
			if (peekCount == 0) {
				peeked[0] = r.Read();
				peekCount = 1;
			}
			return peeked[0];
		}
		
		public int Peek2() {
			Peek();
			if (peekCount == 1) {
				peeked[1] = r.Read();
				peekCount = 2;
			}
			return peeked[1];
		}

		public int Read() {
			int c;
			
			if (peekCount > 0) {
				c = peeked[0];
				peeked[0] = peeked[1];
				peekCount--;
			} else {
				c = r.Read();
			}
			
			if (c == '\n') { Line++; Col = 0; }
			else { Col++; }
			
			return c;
		}
	}

	internal struct Location {
		public readonly int Line, Col;
		public Location(int line, int col) { Line = line; Col = col; }
	}
		
}
