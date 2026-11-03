using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PdfEditorApp.Plugins.CSharpEditor.Services.Display;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Execution;

/// <summary>
/// Makes Stop end a loop that never looks at its cancellation token (<c>while (true) { }</c>). Roslyn only checks the token
/// between statements of the top level, so such a loop used to keep a core busy for the rest of the session, one more thread
/// for every Stop (<see cref="ExecutionAbandonment"/>). The studio inserts <see cref="Tick"/> at the top of every loop body of
/// the C# it runs (<see cref="Instrument"/>); it costs one read of a shared count until a run's Stop is pressed.
/// </summary>
public static class LoopGuard
{
    private const string TickCall = "global::PdfEditorApp.Plugins.CSharpEditor.Services.Execution.LoopGuard.Tick();";

    // How many running executions have had Stop pressed: while it is zero, Tick returns at once.
    private static int s_stopsPending;

    /// <summary>Inserted at the top of every loop body of the code the studio runs: ends the loop when this run was stopped.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Tick()
    {
        if (Volatile.Read(ref s_stopsPending) == 0) return;
        InteractiveCancellationContext.Current.ThrowIfCancellationRequested();
    }

    /// <summary>Counts <paramref name="token"/>'s cancellation (while the returned scope lasts) so <see cref="Tick"/> looks at it.</summary>
    internal static IDisposable Track(CancellationToken token)
    {
        if (!token.CanBeCanceled) return NoScope.Instance;
        return new TrackScope(token);
    }

    /// <summary>
    /// <paramref name="code"/> with <see cref="Tick"/> at the top of every loop body. The call goes on the line of the loop's
    /// opening brace, so line numbers (in errors and stack traces) stay the same. Code that doesn't parse is returned as it
    /// is, so its compile errors point where the user wrote them.
    /// </summary>
    public static string Instrument(string code, SourceCodeKind kind)
    {
        if (string.IsNullOrEmpty(code) || !(code.Contains("while", StringComparison.Ordinal) || code.Contains("for", StringComparison.Ordinal)))
        {
            return code;
        }

        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest, kind: kind));
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return code;

        var rewritten = new Rewriter().Visit(tree.GetRoot());
        return rewritten.ToFullString();
    }

    private sealed class Rewriter : CSharpSyntaxRewriter
    {
        private static StatementSyntax Call() => SyntaxFactory.ParseStatement(TickCall);

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        private static StatementSyntax Guard(StatementSyntax body)
        {
            if (body is BlockSyntax block)
            {
                // "{ Tick();" and then whatever followed the brace on its line (usually the line break).
                var open = block.OpenBraceToken;
                var call = Call().WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(open.TrailingTrivia);
                return block
                    .WithOpenBraceToken(open.WithTrailingTrivia())
                    .WithStatements(block.Statements.Insert(0, call));
            }

            // One statement without braces: braces around it, on its own line(s).
            var wrapped = SyntaxFactory.Block(
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(body.GetLeadingTrivia()).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.List([Call().WithTrailingTrivia(SyntaxFactory.Space), body.WithoutLeadingTrivia().WithoutTrailingTrivia()]),
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(body.GetTrailingTrivia()));
            return wrapped;
        }
    }

    private sealed class TrackScope : IDisposable
    {
        // 0: not stopped, 1: stopped and counted, 2: over.
        private int _state;
        private readonly CancellationTokenRegistration _registration;

        public TrackScope(CancellationToken token)
        {
            _registration = token.Register(() =>
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) == 0) Interlocked.Increment(ref s_stopsPending);
            });
        }

        public void Dispose()
        {
            _registration.Dispose();
            if (Interlocked.Exchange(ref _state, 2) == 1) Interlocked.Decrement(ref s_stopsPending);
        }
    }

    private sealed class NoScope : IDisposable
    {
        public static NoScope Instance { get; } = new();
        public void Dispose() { }
    }
}
