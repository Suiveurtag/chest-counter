import { createContext, useContext } from 'react';

const en = {
  'nav.home': 'Home', 'nav.squad': 'Squad', 'nav.matches': 'Matches', 'nav.insights': 'Insights', 'nav.settings': 'Settings',

  'scope.personal': 'Your chests', 'scope.squad': 'Squad', 'scope.session': 'This session', 'scope.last': 'Last match',
  'home.across': 'in {matches} matches', 'home.noMatches': 'No match counted yet',
  'home.noMatchesHint': 'Finish a Battle Royale match with replays turned on. Its chests are counted a few seconds after it ends.',
  'home.average': 'Average per match', 'home.matches': 'Matches analyzed', 'home.best': 'Best match',
  'home.sessionSince': 'since {time}', 'home.activity': 'Last 30 days', 'home.recent': 'Recent matches',
  'home.squadCombined': 'Everyone’s chests, added up', 'home.squadCta': 'Count with your squad',
  'home.squadCtaHint': 'Each friend runs the app on their own PC. Your totals meet here.',
  'home.squadCtaButton': 'Set up a squad', 'home.lastShared': 'Last match together',
  'home.incomplete': '{n} matches may be missing chests', 'home.destroyed': '{n} destroyed',

  'status.watching': 'Watching for new replays', 'status.scanning': 'Reading replay {index} of {total}',
  'status.recording': 'Match in progress. Chests are counted when it ends.', 'status.settling': 'Match over. Reading its replay…',
  'status.paused': 'Replay folder not found', 'status.lastCounted': 'Last replay read: {chests} chests',
  'status.lastDuplicate': 'Last replay read: already counted', 'status.lastError': 'Last replay could not be read',
  'status.lastNotCounted': 'Last replay read: mode not counted', 'status.lastIncomplete': 'Last replay was incomplete',

  'squad.setupTitle': 'Count chests with your squad',
  'squad.setupLead': 'Everyone runs this app on their own PC. Each PC reads its own replays and shares only the resulting numbers, never the replay files and never anything from your Epic account.',
  'squad.joinTitle': 'Join with an invite', 'squad.joinHint': 'Paste the invite link a friend sent you.',
  'squad.invitePlaceholder': 'https://…/join/ABCD-EFGH', 'squad.join': 'Join squad',
  'squad.createTitle': 'Start a new squad', 'squad.createHint': 'You need the address of a squad server. One of you hosts it; see the README.',
  'squad.server': 'Server address', 'squad.displayName': 'Your name in the squad', 'squad.squadName': 'Squad name', 'squad.create': 'Create squad',
  'squad.registrationKey': 'Registration key (only if the server asks for one)',
  'squad.combined': 'Combined', 'squad.combinedHint': 'Every member’s lifetime chests, whoever they played with.',
  'squad.together': 'Together', 'squad.togetherHint': 'Chests from the {n} matches you played as a team.',
  'squad.members': 'Members', 'squad.you': 'you', 'squad.owner': 'owner', 'squad.online': 'Online', 'squad.offline': 'Offline',
  'squad.lastSync': 'Synced {time}', 'squad.neverSynced': 'Nothing uploaded yet', 'squad.lifetime': 'lifetime', 'squad.withSquad': 'with the squad',
  'squad.lastMatch': 'Last match: {n}', 'squad.emptySlot': 'Open slot', 'squad.remove': 'Remove from squad',
  'squad.history': 'Matches played together', 'squad.historyEmpty': 'No shared match yet',
  'squad.historyEmptyHint': 'A match appears here once at least two of you have played it together and your apps have read its replay.',
  'squad.partial': 'Partial', 'squad.partialHint': 'Waiting for {names}', 'squad.ambiguous': 'Needs review',
  'squad.ambiguousHint': 'The uploads disagree about this match, so it is left out of the totals.',
  'squad.seenByOthers': 'teammates’ replays saw about {n}, not counted',
  'squad.invites': 'Invites', 'squad.newInvite': 'New invite link', 'squad.copy': 'Copy link', 'squad.copied': 'Copied', 'squad.revoke': 'Revoke',
  'squad.inviteUses': '{uses} of {max} used, expires {date}', 'squad.noInvites': 'No active invite.',
  'squad.full': 'The squad is full.', 'squad.leave': 'Leave squad', 'squad.rename': 'Rename',
  'squad.leaveConfirm': 'Leave this squad? Your own history stays on this PC.',
  'squad.state.online': 'Connected', 'squad.state.connecting': 'Connecting…', 'squad.state.offline': 'Server unreachable. Showing the last known state.',
  'squad.pending': '{n} results waiting to upload', 'squad.refused': '{n} results were refused by the server (see Settings, Diagnostics)',
  'squad.warn.start_time_mismatch': 'start times differ', 'squad.warn.playlist_mismatch': 'game modes differ', 'squad.warn.roster_mismatch': 'teammate lists differ',

  'matches.range.7': '7 days', 'matches.range.30': '30 days', 'matches.range.90': '90 days', 'matches.range.all': 'All time',
  'matches.allSessions': 'All sessions', 'matches.session': 'Session of {date}', 'matches.showNotCounted': 'Show matches that are not counted',
  'matches.empty': 'No match in this period', 'matches.when': 'When', 'matches.mode': 'Mode', 'matches.place': 'Place', 'matches.chests': 'Chests',
  'matches.ammo': 'Ammo boxes', 'matches.other': 'Other', 'matches.total': '{chests} chests in {matches} matches',
  'matches.flag.incomplete': 'Fortnite did not record everything a vehicle destroyed in this match: some chests may be missing.',
  'matches.flag.local': 'This replay held no match id, so it cannot be matched with teammates.',
  'matches.reason.unsupported_mode': 'Mode not counted', 'matches.reason.custom_match': 'Custom match', 'matches.reason.no_player': 'Player unknown',
  'matches.detail.kinds': 'What was opened', 'matches.detail.timeline': 'When chests were opened', 'matches.detail.team': 'Teammates, as seen from your replay',
  'matches.detail.teamHint': 'Only what happened near you is recorded. These figures are never added to a total.',
  'matches.detail.treasures': 'Treasures', 'matches.detail.file': 'Replay', 'matches.detail.parser': 'Analyzer version {v}', 'matches.detail.destroyed': 'destroyed',
  'kind.chest': 'Chests', 'kind.rare_chest': 'Rare chests', 'kind.buried_chest': 'Buried chests', 'kind.sprite_chest': 'Sprite chests',
  'kind.poster_chest': 'Poster chests', 'kind.other_chest': 'Other chests', 'kind.loot_cannon': 'Loot cannons', 'kind.ammo_box': 'Ammo boxes',
  'kind.food_box': 'Food boxes', 'kind.other_container': 'Other containers',

  'insights.daily': 'Per day', 'insights.weekly': 'Per week', 'insights.overTime': 'Chests over time', 'insights.cumulative': 'Running total',
  'insights.contributions': 'Who opened what', 'insights.youVsSquad': 'You and the squad', 'insights.busiestDay': 'Busiest day',
  'insights.activeDays': 'Days played', 'insights.perDay': 'Per day played', 'insights.noSquad': 'Join a squad to compare.',
  'insights.empty': 'Nothing to chart yet',

  'settings.replays': 'Replays', 'settings.folder': 'Replay folder', 'settings.folderDefault': 'Fortnite’s own folder', 'settings.choose': 'Choose folder',
  'settings.useDefault': 'Use Fortnite’s folder', 'settings.open': 'Open', 'settings.scan': 'Scan now',
  'settings.recording.likely_on': 'Fortnite is saving replays: the newest one is from {date}.',
  'settings.recording.likely_off': 'Replay recording looks turned off in Fortnite.',
  'settings.recording.no_folder': 'This folder does not exist yet. Fortnite creates it the first time it saves a replay.',
  'settings.recording.no_replays': 'No replay in this folder yet.', 'settings.recording.stale': 'No recent replay: the newest one is from {date}.',
  'settings.recording.unknown': 'Could not check the folder.',
  'settings.recordingHelp': 'In Fortnite: Settings, Game, Replays, turn on “Record Replays”. Fortnite keeps only the most recent unsaved replays, so matches played while this app was closed for a long time may be gone.',
  'settings.startup': 'Startup', 'settings.launchWindows': 'Start with Windows', 'settings.launchFortnite': 'Open when Fortnite starts',
  'settings.launchFortniteHint': 'A tiny background process waits for Fortnite. It only looks at the list of running programs.',
  'settings.startMinimized': 'Start minimized', 'settings.tray': 'Closing the window keeps tracking in the tray',
  'settings.overlay': 'In-game overlay', 'settings.overlayOn': 'Show the overlay', 'settings.overlayLocked': 'Lock in place (clicks pass through to the game)',
  'settings.overlayHint': 'A separate always-on-top window. It works with windowed and borderless modes; exclusive fullscreen hides every overlay. Drag it to move, scroll on it to resize.',
  'settings.opacity': 'Opacity', 'settings.size': 'Size', 'settings.show': 'Show', 'settings.members': 'Each member',
  'settings.squadScope': 'Squad figure', 'settings.scopeCombined': 'Combined lifetime', 'settings.scopeShared': 'Matches together',
  'settings.hotkeys': 'Show or hide: {toggle}. Lock or unlock: {lock}.', 'settings.obs': 'OBS browser source', 'settings.obsHint': 'Add a Browser source with this address. Its background is transparent.',
  'settings.display': 'Language and time', 'settings.language': 'Language', 'settings.timeFormat': 'Clock', 'settings.auto': 'System',
  'settings.profile': 'Account shown', 'settings.profileAuto': 'Most recently played',
  'settings.data': 'Your data', 'settings.dataHint': 'Everything is stored in one file on this PC. Replay files are only ever read.',
  'settings.backup': 'Back up now', 'settings.export': 'Export matches (CSV)', 'settings.openData': 'Open data folder',
  'settings.reanalyze': 'Read all replays again', 'settings.reanalyzeHint': 'Replaces each match with a fresh reading. Matches whose replay Fortnite has deleted are kept as they are.',
  'settings.retry': 'Retry unreadable replays', 'settings.saved': 'Saved to {path}',
  'settings.server': 'Squad server', 'settings.serverNone': 'Not connected. Solo tracking needs no server.', 'settings.disconnect': 'Disconnect this PC',
  'settings.disconnectConfirm': 'Disconnect from the squad server? Your local history is kept.', 'settings.resync': 'Upload everything again',
  'settings.diagnostics': 'Diagnostics', 'settings.files': '{counted} counted, {duplicate} duplicates, {notCounted} in other modes, {incomplete} incomplete, {error} unreadable',
  'settings.problems': 'Replays that could not be counted', 'settings.noProblems': 'Every replay was read.', 'settings.log': 'Log',
  'settings.limits': 'What replays can and cannot tell',
  'settings.limit1': 'Totals appear after a match ends. Fortnite only writes the key that unlocks a replay when it stops recording, so nothing can be counted live.',
  'settings.limit2': 'Only regular Battle Royale (build and zero build) is counted. Ranked, Reload, OG, limited-time modes and Creative are read but left out.',
  'settings.limit3': 'A replay only holds what happened near you. That is why each squad member counts their own chests on their own PC.',
  'settings.limit4': 'Chests destroyed by a fast vehicle are sometimes missing from the replay. Those matches are flagged.',
  'settings.about': 'About', 'settings.aboutText': 'Replay analysis by LED-N/Fortnite-ChestCounter and Shiqan/FortniteReplayDecompressor (MIT). Not affiliated with Epic Games.',
  'settings.version': 'Version {version}, analyzer {parser}',

  'onb.title': 'Let’s find your replays', 'onb.step1': 'Replay folder', 'onb.step2': 'Import', 'onb.step3': 'Ready',
  'onb.found': 'Found {n} replays in Fortnite’s folder', 'onb.notFound': 'Fortnite’s replay folder was not found',
  'onb.notFoundHint': 'Play one match with replays turned on, or choose the folder yourself.', 'onb.start': 'Import replays', 'onb.skipEmpty': 'Continue without replays',
  'onb.importing': 'Reading replay {index} of {total}', 'onb.importDone': 'Import finished',
  'onb.counted': 'Matches counted', 'onb.skipped': 'Not counted', 'onb.unreadable': 'Unreadable', 'onb.chests': 'Chests found',
  'onb.skippedHint': 'Other game modes, copies of a match already counted, and recordings cut short.',
  'onb.leave': 'You can close this window: the import carries on and nothing already read is lost.',
  'onb.finish': 'Open the dashboard', 'onb.squad': 'Set up a squad', 'onb.readyLead': 'From now on, every match you finish is counted on its own.',

  'common.showAll': 'Show all {n}', 'common.cancel': 'Cancel', 'common.close': 'Close', 'common.matches': 'matches', 'common.today': 'Today', 'common.yesterday': 'Yesterday',
  'common.error': 'Something went wrong: {message}',
};

export type Key = keyof typeof en;

const fr: Partial<Record<Key, string>> = {
  'nav.home': 'Accueil', 'nav.squad': 'Escouade', 'nav.matches': 'Parties', 'nav.insights': 'Statistiques', 'nav.settings': 'Réglages',
  'scope.personal': 'Tes coffres', 'scope.squad': 'Escouade', 'scope.session': 'Cette session', 'scope.last': 'Dernière partie',
  'home.across': 'en {matches} parties', 'home.noMatches': 'Aucune partie comptée pour l’instant',
  'home.noMatchesHint': 'Termine une partie de Battle Royale avec les replays activés. Ses coffres sont comptés quelques secondes après la fin.',
  'home.average': 'Moyenne par partie', 'home.matches': 'Parties analysées', 'home.best': 'Meilleure partie',
  'home.sessionSince': 'depuis {time}', 'home.activity': '30 derniers jours', 'home.recent': 'Parties récentes',
  'home.squadCombined': 'Les coffres de tout le monde, additionnés', 'home.squadCta': 'Compte avec ton escouade',
  'home.squadCtaHint': 'Chaque ami lance l’appli sur son PC. Vos totaux se retrouvent ici.',
  'home.squadCtaButton': 'Configurer une escouade', 'home.lastShared': 'Dernière partie ensemble',
  'home.incomplete': '{n} parties où des coffres peuvent manquer', 'home.destroyed': '{n} détruits',
  'status.watching': 'En attente de nouveaux replays', 'status.scanning': 'Lecture du replay {index} sur {total}',
  'status.recording': 'Partie en cours. Les coffres sont comptés à la fin.', 'status.settling': 'Partie terminée. Lecture du replay…',
  'status.paused': 'Dossier des replays introuvable', 'status.lastCounted': 'Dernier replay lu : {chests} coffres',
  'status.lastDuplicate': 'Dernier replay lu : déjà compté', 'status.lastError': 'Le dernier replay est illisible',
  'status.lastNotCounted': 'Dernier replay lu : mode non compté', 'status.lastIncomplete': 'Le dernier replay était incomplet',
  'squad.setupTitle': 'Compte les coffres avec ton escouade',
  'squad.setupLead': 'Chacun lance cette appli sur son PC. Chaque PC lit ses propres replays et ne partage que les chiffres obtenus, jamais les fichiers ni quoi que ce soit de ton compte Epic.',
  'squad.joinTitle': 'Rejoindre avec une invitation', 'squad.joinHint': 'Colle le lien d’invitation envoyé par un ami.', 'squad.join': 'Rejoindre',
  'squad.createTitle': 'Créer une escouade', 'squad.createHint': 'Il faut l’adresse d’un serveur d’escouade. L’un de vous l’héberge ; voir le README.',
  'squad.server': 'Adresse du serveur', 'squad.displayName': 'Ton nom dans l’escouade', 'squad.squadName': 'Nom de l’escouade', 'squad.create': 'Créer l’escouade',
  'squad.registrationKey': 'Clé d’inscription (seulement si le serveur en demande une)',
  'squad.combined': 'Cumulé', 'squad.combinedHint': 'Tous les coffres de chaque membre, peu importe avec qui il jouait.',
  'squad.together': 'Ensemble', 'squad.togetherHint': 'Coffres des {n} parties jouées en équipe.',
  'squad.members': 'Membres', 'squad.you': 'toi', 'squad.owner': 'chef', 'squad.online': 'En ligne', 'squad.offline': 'Hors ligne',
  'squad.lastSync': 'Synchronisé {time}', 'squad.neverSynced': 'Rien d’envoyé pour l’instant', 'squad.lifetime': 'au total', 'squad.withSquad': 'avec l’escouade',
  'squad.lastMatch': 'Dernière partie : {n}', 'squad.emptySlot': 'Place libre', 'squad.remove': 'Retirer de l’escouade',
  'squad.history': 'Parties jouées ensemble', 'squad.historyEmpty': 'Aucune partie commune pour l’instant',
  'squad.historyEmptyHint': 'Une partie apparaît ici dès qu’au moins deux d’entre vous l’ont jouée ensemble et que vos applis ont lu son replay.',
  'squad.partial': 'Partiel', 'squad.partialHint': 'En attente de {names}', 'squad.ambiguous': 'À vérifier',
  'squad.ambiguousHint': 'Les envois ne s’accordent pas sur cette partie : elle est exclue des totaux.',
  'squad.seenByOthers': 'les replays des coéquipiers en ont vu environ {n}, non comptés',
  'squad.invites': 'Invitations', 'squad.newInvite': 'Nouveau lien d’invitation', 'squad.copy': 'Copier le lien', 'squad.copied': 'Copié', 'squad.revoke': 'Révoquer',
  'squad.inviteUses': '{uses} sur {max} utilisées, expire le {date}', 'squad.noInvites': 'Aucune invitation active.',
  'squad.full': 'L’escouade est complète.', 'squad.leave': 'Quitter l’escouade', 'squad.rename': 'Renommer',
  'squad.leaveConfirm': 'Quitter cette escouade ? Ton historique reste sur ce PC.',
  'squad.state.online': 'Connecté', 'squad.state.connecting': 'Connexion…', 'squad.state.offline': 'Serveur injoignable. Dernier état connu affiché.',
  'squad.pending': '{n} résultats en attente d’envoi', 'squad.refused': '{n} résultats refusés par le serveur (voir Réglages, Diagnostic)',
  'matches.range.7': '7 jours', 'matches.range.30': '30 jours', 'matches.range.90': '90 jours', 'matches.range.all': 'Tout',
  'matches.allSessions': 'Toutes les sessions', 'matches.session': 'Session du {date}', 'matches.showNotCounted': 'Afficher les parties non comptées',
  'matches.empty': 'Aucune partie sur cette période', 'matches.when': 'Quand', 'matches.mode': 'Mode', 'matches.place': 'Place', 'matches.chests': 'Coffres',
  'matches.ammo': 'Caisses de munitions', 'matches.other': 'Autres', 'matches.total': '{chests} coffres en {matches} parties',
  'matches.flag.incomplete': 'Fortnite n’a pas tout enregistré de ce qu’un véhicule a détruit : des coffres peuvent manquer.',
  'matches.flag.local': 'Ce replay ne contenait pas d’identifiant de partie : impossible de le relier aux coéquipiers.',
  'matches.reason.unsupported_mode': 'Mode non compté', 'matches.reason.custom_match': 'Partie personnalisée', 'matches.reason.no_player': 'Joueur inconnu',
  'matches.detail.kinds': 'Ce qui a été ouvert', 'matches.detail.timeline': 'Moments d’ouverture', 'matches.detail.team': 'Coéquipiers, vus depuis ton replay',
  'matches.detail.teamHint': 'Seul ce qui se passe près de toi est enregistré. Ces chiffres ne sont jamais ajoutés à un total.',
  'matches.detail.treasures': 'Trésors', 'matches.detail.file': 'Replay', 'matches.detail.parser': 'Analyseur version {v}', 'matches.detail.destroyed': 'détruit',
  'kind.chest': 'Coffres', 'kind.rare_chest': 'Coffres rares', 'kind.buried_chest': 'Coffres enterrés', 'kind.sprite_chest': 'Coffres d’esprit',
  'kind.poster_chest': 'Coffres des affiches', 'kind.other_chest': 'Autres coffres', 'kind.loot_cannon': 'Canons à butin', 'kind.ammo_box': 'Caisses de munitions',
  'kind.food_box': 'Caisses de nourriture', 'kind.other_container': 'Autres conteneurs',
  'insights.daily': 'Par jour', 'insights.weekly': 'Par semaine', 'insights.overTime': 'Coffres dans le temps', 'insights.cumulative': 'Total cumulé',
  'insights.contributions': 'Qui a ouvert quoi', 'insights.youVsSquad': 'Toi et l’escouade', 'insights.busiestDay': 'Meilleur jour',
  'insights.activeDays': 'Jours joués', 'insights.perDay': 'Par jour joué', 'insights.noSquad': 'Rejoins une escouade pour comparer.', 'insights.empty': 'Rien à tracer pour l’instant',
  'settings.replays': 'Replays', 'settings.folder': 'Dossier des replays', 'settings.folderDefault': 'Dossier de Fortnite', 'settings.choose': 'Choisir un dossier',
  'settings.useDefault': 'Utiliser le dossier de Fortnite', 'settings.open': 'Ouvrir', 'settings.scan': 'Analyser maintenant',
  'settings.recording.likely_on': 'Fortnite enregistre les replays : le plus récent date du {date}.',
  'settings.recording.likely_off': 'L’enregistrement des replays semble désactivé dans Fortnite.',
  'settings.recording.no_folder': 'Ce dossier n’existe pas encore. Fortnite le crée au premier replay enregistré.',
  'settings.recording.no_replays': 'Aucun replay dans ce dossier.', 'settings.recording.stale': 'Pas de replay récent : le dernier date du {date}.',
  'settings.recording.unknown': 'Impossible de vérifier le dossier.',
  'settings.recordingHelp': 'Dans Fortnite : Paramètres, Jeu, Replays, active « Enregistrer les replays ». Fortnite ne garde que les replays non sauvegardés les plus récents : des parties jouées pendant que l’appli était fermée longtemps peuvent avoir disparu.',
  'settings.startup': 'Démarrage', 'settings.launchWindows': 'Lancer avec Windows', 'settings.launchFortnite': 'Ouvrir au lancement de Fortnite',
  'settings.launchFortniteHint': 'Un petit processus attend Fortnite en arrière-plan. Il ne regarde que la liste des programmes en cours.',
  'settings.startMinimized': 'Démarrer réduit', 'settings.tray': 'Fermer la fenêtre garde le suivi actif dans la zone de notification',
  'settings.overlay': 'Overlay en jeu', 'settings.overlayOn': 'Afficher l’overlay', 'settings.overlayLocked': 'Verrouiller (les clics traversent vers le jeu)',
  'settings.overlayHint': 'Une fenêtre séparée, toujours au premier plan. Fonctionne en fenêtré et plein écran fenêtré ; le plein écran exclusif masque tous les overlays. Glisse pour déplacer, molette pour redimensionner.',
  'settings.opacity': 'Opacité', 'settings.size': 'Taille', 'settings.show': 'Afficher', 'settings.members': 'Chaque membre',
  'settings.squadScope': 'Chiffre d’escouade', 'settings.scopeCombined': 'Cumul total', 'settings.scopeShared': 'Parties ensemble',
  'settings.hotkeys': 'Afficher ou masquer : {toggle}. Verrouiller : {lock}.', 'settings.obs': 'Source navigateur OBS', 'settings.obsHint': 'Ajoute une source Navigateur avec cette adresse. Son fond est transparent.',
  'settings.display': 'Langue et heure', 'settings.language': 'Langue', 'settings.timeFormat': 'Horloge', 'settings.auto': 'Système',
  'settings.profile': 'Compte affiché', 'settings.profileAuto': 'Le plus récemment joué',
  'settings.data': 'Tes données', 'settings.dataHint': 'Tout est stocké dans un seul fichier sur ce PC. Les replays ne sont jamais modifiés.',
  'settings.backup': 'Sauvegarder', 'settings.export': 'Exporter les parties (CSV)', 'settings.openData': 'Ouvrir le dossier',
  'settings.reanalyze': 'Relire tous les replays', 'settings.reanalyzeHint': 'Remplace chaque partie par une nouvelle lecture. Les parties dont Fortnite a supprimé le replay sont conservées.',
  'settings.retry': 'Réessayer les replays illisibles', 'settings.saved': 'Enregistré dans {path}',
  'settings.server': 'Serveur d’escouade', 'settings.serverNone': 'Non connecté. Le suivi solo n’a besoin d’aucun serveur.', 'settings.disconnect': 'Déconnecter ce PC',
  'settings.disconnectConfirm': 'Se déconnecter du serveur d’escouade ? Ton historique local est conservé.', 'settings.resync': 'Tout renvoyer',
  'settings.diagnostics': 'Diagnostic', 'settings.files': '{counted} comptés, {duplicate} doublons, {notCounted} dans d’autres modes, {incomplete} incomplets, {error} illisibles',
  'settings.problems': 'Replays qui n’ont pas pu être comptés', 'settings.noProblems': 'Tous les replays ont été lus.', 'settings.log': 'Journal',
  'settings.limits': 'Ce que les replays permettent ou non',
  'settings.limit1': 'Les totaux arrivent à la fin de la partie. Fortnite n’écrit la clé du replay qu’à l’arrêt de l’enregistrement : rien ne peut être compté en direct.',
  'settings.limit2': 'Seul le Battle Royale classique (construction et zéro construction) est compté. Classé, Reload, OG, modes temporaires et Créatif sont lus mais exclus.',
  'settings.limit3': 'Un replay ne contient que ce qui s’est passé près de toi. C’est pourquoi chaque membre compte ses coffres sur son propre PC.',
  'settings.limit4': 'Les coffres détruits par un véhicule rapide manquent parfois dans le replay. Ces parties sont signalées.',
  'settings.about': 'À propos', 'settings.aboutText': 'Analyse des replays par LED-N/Fortnite-ChestCounter et Shiqan/FortniteReplayDecompressor (MIT). Sans lien avec Epic Games.',
  'settings.version': 'Version {version}, analyseur {parser}',
  'onb.title': 'Trouvons tes replays', 'onb.step1': 'Dossier', 'onb.step2': 'Import', 'onb.step3': 'Prêt',
  'onb.found': '{n} replays trouvés dans le dossier de Fortnite', 'onb.notFound': 'Le dossier des replays de Fortnite est introuvable',
  'onb.notFoundHint': 'Joue une partie avec les replays activés, ou choisis le dossier toi-même.', 'onb.start': 'Importer les replays', 'onb.skipEmpty': 'Continuer sans replay',
  'onb.importing': 'Lecture du replay {index} sur {total}', 'onb.importDone': 'Import terminé',
  'onb.counted': 'Parties comptées', 'onb.skipped': 'Non comptées', 'onb.unreadable': 'Illisibles', 'onb.chests': 'Coffres trouvés',
  'onb.skippedHint': 'Autres modes de jeu, copies d’une partie déjà comptée et enregistrements interrompus.',
  'onb.leave': 'Tu peux fermer cette fenêtre : l’import continue et rien de ce qui est lu n’est perdu.',
  'onb.finish': 'Ouvrir le tableau de bord', 'onb.squad': 'Configurer une escouade', 'onb.readyLead': 'Désormais, chaque partie terminée est comptée toute seule.',
  'common.showAll': 'Tout afficher ({n})', 'common.cancel': 'Annuler', 'common.close': 'Fermer', 'common.matches': 'parties', 'common.today': 'Aujourd’hui', 'common.yesterday': 'Hier',
  'common.error': 'Un problème est survenu : {message}',
};

export interface Locale {
  lang: 'en' | 'fr';
  hour12?: boolean;
  t: (key: Key, vars?: Record<string, string | number>) => string;
  n: (value: number) => string;
  date: (iso?: string) => string;
  time: (iso?: string) => string;
  dateTime: (iso?: string) => string;
  ago: (iso?: string) => string;
  duration: (seconds?: number) => string;
  playlist: (name?: string) => string;
}

export function makeLocale(lang: 'en' | 'fr', timeFormat: 'auto' | '12' | '24'): Locale {
  const tag = lang === 'fr' ? 'fr-FR' : undefined;
  const hour12 = timeFormat === 'auto' ? undefined : timeFormat === '12';
  const number = new Intl.NumberFormat(tag);
  const dateFmt = new Intl.DateTimeFormat(tag, { day: 'numeric', month: 'short' });
  const timeFmt = new Intl.DateTimeFormat(tag, { hour: 'numeric', minute: '2-digit', hour12 });
  const rel = new Intl.RelativeTimeFormat(tag, { numeric: 'auto' });
  const t: Locale['t'] = (key, vars) => {
    let text = (lang === 'fr' ? fr[key] : undefined) ?? en[key] ?? key;
    if (vars) for (const [k, v] of Object.entries(vars)) text = text.replace(`{${k}}`, String(v));
    return text;
  };
  const dayStart = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const date: Locale['date'] = (iso) => {
    if (!iso) return '';
    const d = new Date(iso);
    const days = Math.round((dayStart(new Date()) - dayStart(d)) / 86400000);
    return days === 0 ? t('common.today') : days === 1 ? t('common.yesterday') : dateFmt.format(d);
  };
  return {
    lang, hour12, t,
    n: (v) => number.format(v),
    date,
    time: (iso) => (iso ? timeFmt.format(new Date(iso)) : ''),
    dateTime: (iso) => (iso ? `${date(iso)}, ${timeFmt.format(new Date(iso))}` : ''),
    ago: (iso) => {
      if (!iso) return '';
      const s = (new Date(iso).getTime() - Date.now()) / 1000;
      const abs = Math.abs(s);
      if (abs < 60) return rel.format(0, 'minute');
      if (abs < 3600) return rel.format(Math.round(s / 60), 'minute');
      if (abs < 86400) return rel.format(Math.round(s / 3600), 'hour');
      return rel.format(Math.round(s / 86400), 'day');
    },
    duration: (seconds) => (seconds == null ? '' : `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`),
    playlist: (name) => {
      if (!name) return '';
      const zero = /NoBuild/i.test(name);
      const size = /Solo/i.test(name) ? 'Solo' : /Duo/i.test(name) ? 'Duo' : /Trio/i.test(name) ? 'Trio' : /Squad/i.test(name) ? (lang === 'fr' ? 'Section' : 'Squad') : name.replace(/^Playlist_/, '');
      return zero ? `${size}, ${lang === 'fr' ? 'zéro construction' : 'zero build'}` : size;
    },
  };
}

export const LocaleContext = createContext<Locale>(makeLocale('en', 'auto'));
export const useLocale = () => useContext(LocaleContext);
