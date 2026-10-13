import { memo, useState, type ReactNode } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { Check, Copy } from "lucide-react";

// Model output is untrusted. react-markdown never renders raw HTML (no rehype-raw) and strips
// javascript: URLs; links leave the app only through the shell's native confirmation.

const components: Components = {
  pre: ({ children }) => <CodeBlock>{children}</CodeBlock>,
  a: ({ href, children }) => (
    <a href={href} target="_blank" rel="noreferrer noopener">
      {children}
    </a>
  ),
  table: ({ children }) => (
    <div className="md-table">
      <table>{children}</table>
    </div>
  ),
};

export const Markdown = memo(function Markdown({ text }: { text: string }) {
  return (
    <div className="md">
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {text}
      </ReactMarkdown>
    </div>
  );
});

function CodeBlock({ children }: { children: ReactNode }) {
  const [copied, setCopied] = useState(false);
  const child = children as { props?: { className?: string; children?: ReactNode } };
  const language = /language-(\S+)/.exec(child?.props?.className ?? "")?.[1];
  const code = String(child?.props?.children ?? "").replace(/\n$/, "");

  const copy = async () => {
    await navigator.clipboard.writeText(code);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  return (
    <div className="code">
      <div className="code__bar">
        <span>{language ?? "текст"}</span>
        <button className="code__copy" onClick={() => void copy()}>
          {copied ? <Check size={13} /> : <Copy size={13} />}
          {copied ? "Скопировано" : "Копировать"}
        </button>
      </div>
      <pre>
        <code>{code}</code>
      </pre>
    </div>
  );
}
