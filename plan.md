project name: iceghost

the problem:
I use multiple llm agents (pi, claude code, codex). I'll start a session in a project folder, create a task for it and walk away. Later I come back to see it is in one of many states (completed, stuck pending my response, hung) and have to act on it. The computer may reboot itself, and I lose the session. If it's hung, I may have to endtask it and lose the session. 

the goal: 
I would like both an electron app and a mobile app I could open up and see the most recent sessions on multiple computers and their state. I could then tap/click into a session and ask it a question/give it a new task and it would pass it to the session or resume it if stopped. This would require multiple things, in my opinion:
- a server they could all coummincate with.
    - I have a k8s cluster I could run this on. investigate ~/web-apps for how I create web apps and your-gitops-repo for how I deploy.
- a runner daemon on pc that scans all sessions and reports to the server and listens for session commands
    - I use Windows, Mac, and ubuntu linux