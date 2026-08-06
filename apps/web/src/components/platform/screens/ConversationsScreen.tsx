import { useRef, useEffect, type ReactNode } from "react";
import { Thread, Message, chMeta } from "../../../data/console";
import { Chan } from "../../ui/StatusTags";
import { Chip } from "../../ui/Chip";
import { ConsentBadge } from "../../ui/ConsentBadge";
import { Icon } from "../../ui/Icon";
import { PrimaryButton } from "../../ui/Button";

interface ThreadRowProps {
  t: Thread;
  active: boolean;
  isDesktop: boolean;
  onClick: () => void;
}

function ThreadRow({ t, active, isDesktop, onClick }: ThreadRowProps) {
  const cm = chMeta(t.channel);
  return (
    <button
      onClick={onClick}
      className="block w-full cursor-pointer rounded-[13px] p-[12px_13px] mb-[9px] text-left transition-all duration-150 hover:shadow-[0_8px_24px_-10px_rgba(10,10,10,0.18)]"
      style={{
        border: `1px solid ${active && isDesktop ? '#0A0A0A' : '#EAEAEA'}`,
        background: active && isDesktop ? '#FAFAF9' : '#fff',
        fontFamily: "'Inter Tight', system-ui, sans-serif",
      }}
    >
      <div className="flex items-center gap-[10px]">
        <Chan channel={t.channel} size={34} radius={10} />
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-[6px]">
            {t.live && (
              <span className="w-[7px] h-[7px] rounded-full bg-[#2F6BEB] shrink-0" />
            )}
            <span className="text-[13.5px] font-semibold text-ink whitespace-nowrap overflow-hidden text-ellipsis">{t.name}</span>
            <span className="font-mono ml-auto text-[10px] text-t3 shrink-0">{t.ago}</span>
          </div>
          <div className="flex items-center gap-[5px] mt-[2px]">
            <span className="w-[7px] h-[7px] rounded-full" style={{ background: cm.color }} />
            <span className="font-mono text-[9px] tracking-[0.08em] uppercase text-t3">{cm.label}</span>
          </div>
        </div>
      </div>
      <div className="text-[12.5px] text-t2 mt-[9px] leading-[1.4]">{t.preview}</div>
      {/* An ended session shows a status only when one is actually recorded. The session
          summary carries no outcome, so live rows previously all read "Pending". */}
      <div className="mt-[10px] flex items-center gap-[7px] flex-wrap">
        {t.live
          ? <span className="inline-flex items-center gap-[5px] text-[10.5px] font-semibold px-[9px] py-[3px] rounded-full"
              style={{ color: '#2056CC', background: '#E9F0FE' }}>
              <span className="w-[6px] h-[6px] rounded-full bg-[#2F6BEB]" />
              Live · needs you
            </span>
          : t.status && <Chip status={t.status} />}
        {/* The session read has always carried `consent_state`; nothing rendered it, so whether
            the guest had consented — the record that later proves messaging or recording was
            permitted — was invisible. Displayed only: consent is the guest's to give. */}
        <ConsentBadge state={t.consentState} recordingAllowed={t.recordingAllowed} />
      </div>
    </button>
  );
}

interface ConversationThreadProps {
  device: 'phone' | 'desktop';
  thread: Thread;
  msgs: Message[];
  onBack: () => void;
  onTakeOver: () => void;
  /** Replaces the message list when the transcript could not be read. */
  notice?: ReactNode;
  /** True while the transcript is loading. */
  pending?: boolean;
}

function ConversationThread({ device, thread, msgs, onBack, onTakeOver, notice, pending }: ConversationThreadProps) {
  const isPhone = device === 'phone';
  const cm = chMeta(thread.channel);
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [msgs]);

  return (
    <div style={isPhone
      ? { display: 'block', marginTop: 4 }
      : { flex: 1, minWidth: 0, border: '1px solid #EAEAEA', borderRadius: 14, overflow: 'hidden', display: 'flex', flexDirection: 'column', maxHeight: 620 }
    }>
      {/* Thread header */}
      <div className="flex items-center gap-[10px] p-[13px_15px] border-b border-hair bg-white shrink-0">
        {isPhone && (
          <button
            onClick={onBack}
            className="w-8 h-8 border border-cd-border rounded-[8px] bg-white cursor-pointer flex items-center justify-center shrink-0"
          >
            <Icon name="chevL" size={16} strokeWidth={2.2} color="#0A0A0A" />
          </button>
        )}
        <Chan channel={thread.channel} size={36} radius={10} />
        <div className="min-w-0 flex-1">
          <div className="text-[15px] font-semibold text-ink">{thread.name}</div>
          {/* "handled by AI" is only stated for a thread that actually records provenance
              (demo rows). Nothing server-side records who handled a session, so a live thread
              claims nothing. */}
          <span className="font-mono text-[9.5px] tracking-[0.08em] uppercase text-t3 mt-[1px] block">
            {cm.label}{thread.prov === 'ai' ? ' · handled by AI' : ''}
          </span>
        </div>
        {thread.prov === 'ai' && (
          <span className="inline-flex items-center gap-1 text-[10.5px] font-semibold px-2 py-[3px] rounded-[7px] bg-ink text-white shrink-0">
            <Icon name="star" size={9} fill="#DDF247" />AI
          </span>
        )}
      </div>

      {/* Messages */}
      <div className="cd-scroll p-[15px] flex flex-col gap-[10px] flex-1 min-h-0 overflow-y-auto" style={{ background: '#FCFCFB' }}>
        {notice}
        {!notice && pending && (
          <div className="text-[12.5px] text-t3 py-4 text-center">Loading the transcript…</div>
        )}
        {!notice && !pending && msgs.map((m, i) => {
          if (m.from === 'cust') return (
            <div key={i} className="flex justify-end">
              <div className="max-w-[82%] text-[13.5px] leading-[1.45] text-white px-[13px] py-[9px]"
                style={{ background: '#0A0A0A', borderRadius: '16px 16px 5px 16px' }}>
                {m.text}
              </div>
            </div>
          );
          if (m.from === 'agent') return (
            <div key={i} className="flex justify-start">
              <div className="max-w-[82%] text-[13.5px] leading-[1.45] text-ink px-[13px] py-[9px]"
                style={{ background: '#F1F1EF', borderRadius: '16px 16px 16px 5px' }}>
                {m.text}
              </div>
            </div>
          );
          if (m.from === 'system') return (
            <div key={i} className="flex justify-center">
              <div className="font-mono text-[10px] tracking-[0.04em] px-[11px] py-[5px] rounded-full text-center"
                style={{ background: '#F5F5F5', color: '#6B6B6B' }}>
                {m.text}
              </div>
            </div>
          );
          if (m.from === 'readback') return (
            <div key={i} className="flex justify-start">
              <div className="max-w-[90%] text-[13.5px] leading-[1.45] rounded-[14px] px-[13px] py-[10px]"
                style={{ background: '#FBFEE9', border: '1px solid #E4F08A', color: '#3F4A00' }}>
                <div className="flex items-center gap-1 font-mono text-[9px] tracking-[0.12em] uppercase mb-[5px]" style={{ color: '#7E8A2E' }}>
                  <Icon name="star" size={10} fill="#9DB100" />
                  Agent read-back
                </div>
                {m.text}
              </div>
            </div>
          );
          if (m.from === 'escalate') return (
            <div key={i}>
              <div className="rounded-[12px] p-[11px_13px] flex gap-[9px] items-start text-[13px] leading-[1.45]"
                style={{ background: '#FBF3DF', border: '1px solid #F0DCA8', color: '#7E5606' }}>
                <Icon name="alert" size={17} strokeWidth={2} color="#C98A09" style={{ flexShrink: 0, marginTop: 1 }} />
                <span>{m.text}</span>
              </div>
            </div>
          );
          return null;
        })}
        <div ref={bottomRef} />
      </div>

      {/* Footer */}
      {thread.live ? (
        <div className="p-[12px_14px] border-t border-hair bg-white shrink-0">
          <PrimaryButton full onClick={onTakeOver} style={{ height: 44, borderRadius: 11, fontSize: 14 }}>
            <Icon name="takeover" size={16} strokeWidth={2.2} color="#DDF247" />
            Take over this chat
          </PrimaryButton>
        </div>
      ) : (
        /*
          No composer. The input and Send button implied the operator could reply to a guest from
          here; the button had no `onClick` at all, and there is no send-message endpoint anywhere
          in the 50-operation contract — conversations are a read surface. An affordance that
          silently does nothing is the same false claim as a fabricated number, made about a
          capability instead of a fact.
        */
        <div className="p-[12px_14px] border-t border-hair bg-white shrink-0">
          <div className="flex items-center gap-[8px] text-[12px] text-t3 leading-[1.5]">
            <Icon name="alert" size={14} strokeWidth={2} color="#9A9A9A" />
            <span>
              This conversation is read-only. Replying from the console isn’t available yet —
              the platform has no endpoint for it.
            </span>
          </div>
        </div>
      )}
    </div>
  );
}

interface ConversationsScreenProps {
  device: 'phone' | 'desktop';
  threads: Thread[];
  messages: Record<string, Message[]>;
  convOpen: string;
  convView: 'list' | 'thread';
  openConv: (id: string) => void;
  backConv: () => void;
  takeOver: () => void;
  /**
   * Rendered in place of the message list when the transcript could not be read.
   *
   * Without this the pane fell back to the mock `MESSAGES` map, which has no entry for a real
   * session id — so a failed or forbidden read of the most sensitive data in the product
   * displayed a clean, empty, confident conversation.
   */
  transcriptError?: ReactNode;
  /** Shown while the transcript for an open live session is loading. */
  transcriptPending?: boolean;
}

export function ConversationsScreen({
  device, threads, messages, convOpen, convView, openConv, backConv, takeOver,
  transcriptError, transcriptPending,
}: ConversationsScreenProps) {
  const isDesktop = device !== 'phone';
  const active = threads.find(t => t.id === convOpen) || threads[0];
  const showList = isDesktop || convView === 'list';
  const showThread = isDesktop || convView === 'thread';

  return (
    <div style={isDesktop ? { display: 'flex', gap: 18, alignItems: 'flex-start' } : { display: 'block' }}>
      {showList && (
        <div style={isDesktop ? { width: 300, flexShrink: 0, background: '#fff', borderRadius: 14, padding: 8, border: '1px solid #EAEAEA' } : { width: '100%' }}>
          {threads.map(t => (
            <ThreadRow
              key={t.id}
              t={t}
              active={t.id === convOpen}
              isDesktop={isDesktop}
              onClick={() => openConv(t.id)}
            />
          ))}
        </div>
      )}
      {showThread && active && (
        <ConversationThread
          device={device}
          thread={active}
          msgs={messages[active.id] || []}
          onBack={backConv}
          onTakeOver={takeOver}
          notice={transcriptError}
          pending={transcriptPending}
        />
      )}
    </div>
  );
}
